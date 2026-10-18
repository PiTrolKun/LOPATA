using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class ImageGenerationFailureRecoveryTests
{
    [TestMethod]
    public void LegacyMissingEngineFieldNormalizesButUnknownParametersAndDifferentSetDoNot()
    {
        using var files = new MusicProjectTests.Files(); var session = ImageGenerationSessionStore.Create(files.Root);
        var request = new ImageGenerationRequest(Guid.NewGuid().ToString("N"), "z-image", "legacy", 512, 512, [42], "models", session);
        ImageGenerationSessionStore.AddTurn(request);
        var bytes = File.ReadAllBytes(Path.Combine(session, "session.json"));
        var legacy = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(request))!.AsObject();
        legacy.Remove(nameof(ImageGenerationRequest.EngineSetId));
        var store = new BackgroundOperationStore(Path.Combine(files.Root, "background.json"));
        var state = new BackgroundOperationState { Kind = ImageGenerationCatalog.BackgroundKind, Title = "Generation", Project = session,
            Input = JsonSerializer.SerializeToElement(legacy), Phase = BackgroundOperationPhase.Waiting,
            RequiresDecision = true, Detail = "InvalidOperationException" };
        File.WriteAllText(Path.Combine(session, request.Id + "_0.runtime.log"), "Exit: 0xC0000409\n");
        var unknown = legacy.DeepClone().AsObject(); unknown["UnknownGenerationParameter"] = "do not discard";
        store.Save(state with { Input = JsonSerializer.SerializeToElement(unknown) });
        var controller = new BackgroundOperationController(store); controller.Load();
        Assert.IsFalse(ImageGenerationFailureRecovery.Recover(controller), "Unknown execution parameters must not be ignored.");
        var differentSet = legacy.DeepClone().AsObject(); differentSet[nameof(ImageGenerationRequest.EngineSetId)] = "002";
        store.Save(state with { Input = JsonSerializer.SerializeToElement(differentSet) }); controller.Load();
        Assert.IsFalse(ImageGenerationFailureRecovery.Recover(controller), "A different executor is a different operation.");
        store.Save(state); controller.Load();
        Assert.IsTrue(ImageGenerationFailureRecovery.Recover(controller));
        Assert.IsFalse(controller.HasPending);
        Assert.IsFalse(controller.State!.Input.TryGetProperty(nameof(ImageGenerationRequest.EngineSetId), out _), "The original legacy input must not be rewritten.");
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(Path.Combine(session, "session.json")));
    }

    [TestMethod]
    public void RestartRecognizesOnlyTheMatchingFailedNativeAttemptAndPreservesTheSession()
    {
        using var files = new MusicProjectTests.Files(); var session = ImageGenerationSessionStore.Create(files.Root);
        var request = new ImageGenerationRequest(Guid.NewGuid().ToString("N"), "z-image", "кот и компьютер", 4096, 4096, [42], "models", session);
        ImageGenerationSessionStore.AddTurn(request);
        var sessionBytes = File.ReadAllBytes(Path.Combine(session, "session.json"));
        var store = new BackgroundOperationStore(Path.Combine(files.Root, "background.json"));
        var input = new BackgroundOperationState { Kind = ImageGenerationCatalog.BackgroundKind, Title = "Generation", Project = session,
            Input = JsonSerializer.SerializeToElement(request), Phase = BackgroundOperationPhase.Waiting,
            RequiresDecision = true, Detail = "InvalidOperationException", Checkpoint = JsonSerializer.SerializeToElement(new { Completed = 0 }) };
        var log = Path.Combine(session, request.Id + "_0.runtime.log");
        store.Save(input); var controller = new BackgroundOperationController(store); controller.Load();
        Assert.IsFalse(ImageGenerationFailureRecovery.Recover(controller), "Missing log is not proof of executor failure.");
        File.WriteAllText(log, "Exit: 0x00000000\n");
        Assert.IsFalse(ImageGenerationFailureRecovery.Recover(controller));
        File.WriteAllText(log, "Exit: 0xC0000409\nGGML_ASSERT(maxComputeWorkGroupCount) failed\nCPU retry:\nExit: 0x00000000\n");
        Assert.IsFalse(ImageGenerationFailureRecovery.Recover(controller), "Successful last retry must not be discarded.");
        File.WriteAllText(log, "Exit: 0xC0000409\nGGML_ASSERT(maxComputeWorkGroupCount) failed\n");
        store.Save(input with { UserPaused = true }); controller.Load();
        Assert.IsFalse(ImageGenerationFailureRecovery.Recover(controller), "An explicit pause is protected.");
        store.Save(input with { Input = JsonSerializer.SerializeToElement(request with { Prompt = "changed" }) }); controller.Load();
        Assert.IsFalse(ImageGenerationFailureRecovery.Recover(controller), "An unrelated session cannot release the gate.");
        store.Save(input); controller.Load(); Assert.IsTrue(ImageGenerationFailureRecovery.Recover(controller));
        Assert.IsFalse(controller.HasPending); Assert.AreEqual(BackgroundOperationPhase.Failed, controller.State!.Phase);
        Assert.IsTrue(JsonElement.DeepEquals(input.Input, controller.State.Input));
        Assert.IsTrue(JsonElement.DeepEquals(input.Checkpoint!.Value, controller.State.Checkpoint!.Value));
        CollectionAssert.AreEqual(sessionBytes, File.ReadAllBytes(Path.Combine(session, "session.json")));
        controller.Load(); Assert.IsFalse(controller.HasPending); Assert.IsFalse(ImageGenerationFailureRecovery.Recover(controller));
        using (controller.BeginAuxiliary()) { }
    }
}
