using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHub.Services.SongClip;

namespace AIHub.Tests;

[TestClass]
public sealed class SongClipWorkerRequestTests
{
    [TestMethod]
    [DataRow("glitchframe")]
    [DataRow("h3_music_context")]
    public void LegacyPayloadOmitsTheNewPlanWithoutChangingAnyOtherValue(string model)
    {
        using var files = new VideoPreparationTests.TestFiles();
        var path = files.File("request.json");
        var payload = new
        {
            modelId = model, prompt = "Keep the original request verbatim.", lyrics = "Слова песни",
            width = 608, seed = 123, visualPlan = (SongClipVisualPlan?)null,
            references = (object[]?)null, referencePrompts = (string[]?)null,
            existingNullableField = (string?)null, settings = new { rawFrames = 243, contextFrames = 39 }
        };
        SongClipWorkerRequest.Write(path, payload, null);
        var actual = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var expected = JsonSerializer.SerializeToNode(payload)!.AsObject();
        expected.Remove("visualPlan");
        expected.Remove("references"); expected.Remove("referencePrompts");
        Assert.IsFalse(actual.ContainsKey("visualPlan"), "A present JSON null is invalid for the strict Python plan validator.");
        Assert.IsTrue(actual.ContainsKey("existingNullableField"), "This compatibility rule must not drop unrelated null fields.");
        Assert.IsTrue(JsonNode.DeepEquals(expected, actual));
    }

    [TestMethod]
    public void RoleAwarePayloadRetainsEveryOrderedImageAndItsExactStructuredPrompt()
    {
        using var files = new VideoPreparationTests.TestFiles();
        var store = new SongClipStore(files.Root);
        var attempt = new SongClipAttempt(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
            "attachments/song.wav", "attachments/performer.png", "Reference fixture", "", "", new(), files.Root, 8)
            { Model = H3MusicRunner.ModelId, PlanningVersion = 1, References = [
                new("attachments/performer.png", "performer", "Эта певица исполняет песню."),
                new("attachments/place.png", "world", "Здесь происходят события.")] };
        var plan = new SongClipVisualPlan { GlobalStyle = "A realistic cinematic setting with natural light.",
            Scenes = [new(1, 0, 10, "The performer sings while a cat enters the apartment.", [])],
            ReferenceRoles = [new(1, "performer", "The performer shown sings the supplied song."),
                new(2, "world", "The depicted place is the setting of the song events.")] };
        var prompt = SongClipReferencePrompt.Compose(attempt, plan, 1);
        var path = files.File("role-aware.json");
        SongClipWorkerRequest.Write(path, new { references = H3MusicRunner.ReferencePayload(attempt, store),
            referencePrompts = new[] { prompt } }, plan);
        using var request = JsonDocument.Parse(File.ReadAllText(path));
        var references = request.RootElement.GetProperty("references");
        Assert.AreEqual(2, references.GetArrayLength());
        Assert.AreEqual(store.Resolve(attempt.ProjectId, "attachments/performer.png"), references[0].GetProperty("file").GetString());
        Assert.AreEqual("world", references[1].GetProperty("kind").GetString());
        Assert.AreEqual("Здесь происходят события.", references[1].GetProperty("description").GetString());
        Assert.AreEqual(prompt, request.RootElement.GetProperty("referencePrompts")[0].GetString());
        StringAssert.Contains(prompt, "<Picture 1>"); StringAssert.Contains(prompt, "<Picture 2>");
        StringAssert.Contains(prompt, "retention_analysis:"); StringAssert.Contains(prompt, "detailed_description:");
    }

    [TestMethod]
    [DataRow("image")]
    [DataRow("role")]
    [DataRow("prompt")]
    [DataRow("missing")]
    [DataRow("order")]
    public async Task RoleAwareReceiptMustMatchEveryImageRoleAndCanonicalPrompt(string change)
    {
        using var files = new VideoPreparationTests.TestFiles();
        var store = new SongClipStore(files.Root);
        var attempt = new SongClipAttempt(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
            "attachments/song.wav", "attachments/performer.png", "Reference receipt", "", "", new(), files.Root, 8)
            { Model = H3MusicRunner.ModelId, PlanningVersion = 1, References = [
                new("attachments/performer.png", "performer", "Певица исполняет песню."),
                new("attachments/place.png", "world", "Место событий песни.")] };
        foreach (var reference in attempt.References!) {
            var file = store.Resolve(attempt.ProjectId, reference.File); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, reference.File);
        }
        var plan = new SongClipVisualPlan { GlobalStyle = "A realistic cinematic setting with natural light.",
            Scenes = [new(1, 0, 10, "The performer sings while a cat enters the apartment.", [])],
            ReferenceRoles = [new(1, "performer", "The referenced performer sings the supplied song."),
                new(2, "world", "The depicted place is the setting of the song events.")] };
        var receipt = JsonSerializer.SerializeToNode(new {
            referenceInputs = attempt.References!.Select((reference, index) => new { index = index + 1,
                kind = reference.Kind, description = reference.Description,
                sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(store.Resolve(attempt.ProjectId, reference.File)))) }).ToArray(),
            referencePromptSha256 = new[] { Convert.ToHexStringLower(SHA256.HashData(
                Encoding.UTF8.GetBytes(SongClipReferencePrompt.Compose(attempt, plan, 1)))) }
        })!.AsObject();
        await H3MusicRunner.ValidateReferenceReceiptAsync(JsonSerializer.SerializeToElement(receipt), attempt, store, plan, default);
        if (change == "image") File.AppendAllText(store.Resolve(attempt.ProjectId, attempt.References![1].File), "modified");
        if (change == "role") receipt["referenceInputs"]![1]!["description"] = "A different role.";
        if (change == "prompt") receipt["referencePromptSha256"]![0] = "unrelated";
        if (change == "missing") receipt.Remove("referenceInputs");
        if (change == "order") { var first = receipt["referenceInputs"]![0]!.DeepClone();
            receipt["referenceInputs"]![0] = receipt["referenceInputs"]![1]!.DeepClone(); receipt["referenceInputs"]![1] = first; }
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => H3MusicRunner.ValidateReferenceReceiptAsync(
            JsonSerializer.SerializeToElement(receipt), attempt, store, plan, default));
    }

    [TestMethod]
    public async Task LegacyAndEmptyReferenceReceiptsKeepTheirDistinctContracts()
    {
        using var files = new VideoPreparationTests.TestFiles();
        var store = new SongClipStore(files.Root);
        var legacy = new SongClipAttempt(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
            "song.wav", "", "Legacy receipt", "", "", new(), files.Root, 8) { Model = H3MusicRunner.ModelId };
        await H3MusicRunner.ValidateReferenceReceiptAsync(JsonSerializer.SerializeToElement(new { }), legacy, store, null, default);
        var empty = legacy with { PlanningVersion = 1, References = [] };
        await H3MusicRunner.ValidateReferenceReceiptAsync(JsonSerializer.SerializeToElement(
            new { referenceInputs = Array.Empty<object>(), referencePromptSha256 = Array.Empty<string>() }), empty, store, null, default);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => H3MusicRunner.ValidateReferenceReceiptAsync(
            JsonSerializer.SerializeToElement(new { }), empty, store, null, default));
    }

    [TestMethod]
    public void PlannedPayloadContainsTheCompleteActualPlan()
    {
        using var files = new VideoPreparationTests.TestFiles();
        var path = files.File("request.json");
        var plan = new SongClipVisualPlan
        {
            InputHash = "bound-input", GlobalStyle = "A warm animated film with soft amber lighting.",
            Scenes = [new(1, 0, 10, "A ginger cat opens a wooden door in the old house.", ["Кот открыл дверь."])]
        };
        SongClipWorkerRequest.Write(path, new { prompt = "", visualPlan = (SongClipVisualPlan?)null, fps = 24 }, plan);
        using var actual = JsonDocument.Parse(File.ReadAllText(path));
        Assert.IsTrue(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(plan), actual.RootElement.GetProperty("visualPlan")));
        Assert.AreEqual("", actual.RootElement.GetProperty("prompt").GetString());
        Assert.AreEqual(24, actual.RootElement.GetProperty("fps").GetInt32());
    }
}
