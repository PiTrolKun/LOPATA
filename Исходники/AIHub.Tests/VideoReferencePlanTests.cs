using System.IO;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.VideoPreparation;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoReferencePlanTests
{
    internal static VideoPreparationProject Root()
    {
        var p = VideoWorldTests.Root(); p.CurrentStep = "references";
        VideoReferences.Ensure(p).Choice = "create"; VideoReferencePlans.Ensure(p).Mode = "assisted"; return p;
    }
    internal static VideoConceptRequest Request(VideoPreparationProject p, VideoReferencePrompt? item = null) => new(
        Guid.NewGuid().ToString("N"), p.Id, "project.lopataAIVid", p.IdeaRevisionId,
        item is null ? "[]" : VideoReferencePlans.Snapshot(item), p.Form, "core", "test.gguf", 1,
        VideoReferencePromptProtocol.Version, EngineSetSelection.ActiveId,
        item is null ? VideoReferencePromptProtocol.ListPurpose : VideoReferencePromptProtocol.PromptPurpose,
        ContextRevisionId: p.World!.RevisionId, ContextText: VideoReferencePlans.Context(p), ResponseLanguage: "ru-RU", MaterialId: item?.Id ?? "");
    internal static VideoConceptAnswer ListAnswer(VideoPreparationProject p) => new(JsonSerializer.Serialize(new { references = new[] {
        new { name = "Кот-часовщик", cardId = p.World!.Cards[0].Id, purpose = "appearance", brief = "Полный рост, очки." } } }),
        "", "", VideoConceptTests.Model with { Role = "video reference discovery" });
    internal static VideoConceptAnswer PromptAnswer() => new("{\"native\":\"Рыжий кот в круглых очках.\",\"english\":\"Ginger cat with round glasses.\"}",
        "", "", VideoConceptTests.Model with { Role = "video reference drawing prompt" });
    [TestMethod]
    public void DiscoveryIsStrictRejectsForeignIdsAndExposesItsArray()
    {
        var p = Root(); var request = Request(p); var answer = ListAnswer(p);
        Assert.HasCount(1, VideoReferencePromptProtocol.ReadList(answer.Text, request.ContextText));
        var foreign = answer.Text.Replace(p.World!.Cards[0].Id, Guid.NewGuid().ToString("N"));
        Assert.ThrowsExactly<InvalidDataException>(() => VideoReferencePromptProtocol.ReadList(foreign, request.ContextText));
        foreach (var raw in new[] { "[]", "{\"references\":[] ,\"extra\":true}", "{\"references\":[],\"references\":[]}" })
            Assert.ThrowsExactly<InvalidDataException>(() => VideoReferencePromptProtocol.ReadList(raw, request.ContextText));
        Assert.ThrowsExactly<InvalidDataException>(() => VideoReferencePromptProtocol.ReadPrompt("{\"native\":\"x\",\"english\":null}"));
        var envelope = JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "length", message = new { content = answer.Text } } } });
        Assert.ThrowsExactly<InvalidDataException>(() => VideoReferencePromptProtocol.Parse(envelope, answer.Model, request));
    }
    [TestMethod]
    public void DiscoverAgainKeepsManualItemsAndRepeatedReceiptIsIdempotent()
    {
        var p = Root(); var manual = VideoReferencePlans.Add(p); manual.Name = "Мой интерьер";
        var request = Request(p); var answer = ListAnswer(p);
        VideoReferencePlans.Apply(p, request, answer); VideoReferencePlans.Apply(p, request, answer);
        Assert.HasCount(2, p.References!.Plan!.Items); Assert.HasCount(1, p.References.Plan.Proposals);
        VideoReferencePlans.Apply(p, request with { Id = Guid.NewGuid().ToString("N") }, answer);
        Assert.HasCount(2, p.References.Plan.Items); Assert.AreEqual("Мой интерьер", manual.Name); p.Validate();
    }
    [TestMethod]
    public void LateAnswerGoesToHistoryAndCannotOverwriteNativeOrEnglishEdits()
    {
        var p = Root(); var item = VideoReferencePlans.Add(p); item.Name = "Cat";
        var request = Request(p, item); item.English = "My manually written prompt.";
        VideoReferencePlans.Apply(p, request, PromptAnswer()); VideoReferencePlans.Apply(p, request, PromptAnswer());
        Assert.AreEqual("My manually written prompt.", item.English); Assert.AreEqual("", item.Native);
        Assert.AreEqual(1, item.History.Count(v => v.Id == request.Id)); Assert.IsFalse(VideoReferencePlans.Ready(p, item)); p.Validate();
    }
    [TestMethod]
    public void SourceChangeAndDeletedCardRequireReviewWithoutLosingHistory()
    {
        var p = Root(); var item = VideoReferencePlans.Add(p); item.Name = "Cat"; item.CardId = p.World!.Cards[0].Id;
        VideoReferencePlans.Apply(p, Request(p, item), PromptAnswer()); Assert.IsTrue(VideoReferencePlans.Ready(p));
        p.IdeaText += " New requirement."; VideoReferencePlans.Capture(p); Assert.IsFalse(VideoReferencePlans.Ready(p));
        item.Native += " Ручное уточнение."; VideoReferencePlans.Capture(p); Assert.IsTrue(VideoReferencePlans.Ready(p));
        p.World.Cards.Clear(); Assert.IsFalse(VideoReferencePlans.Ready(p, item)); Assert.IsTrue(item.History.Count >= 2);
    }
    [TestMethod]
    public void UndoRedoKeepsSuccessorsAndSaveCarriesActualModelsAndRawReply()
    {
        using var files = new VideoPreparationTests.TestFiles(); var p = Root();
        VideoReferencePlans.Apply(p, Request(p), ListAnswer(p)); var item = p.References!.Plan!.Items.Single();
        VideoReferencePlans.Apply(p, Request(p, item), PromptAnswer()); var first = item.Native;
        item.Native += " Новый фартук."; item.English += " New apron."; VideoReferencePlans.Capture(p);
        Assert.IsTrue(VideoReferencePlans.Move(p, item, -1)); Assert.AreEqual(first, item.Native);
        Assert.IsTrue(VideoReferencePlans.Move(p, item, 1)); StringAssert.EndsWith(item.English, "New apron.");
        var path = files.File("set.lopataAIVid"); VideoPreparationArchive.Save(p, path);
        var read = VideoPreparationArchive.Load(path, files.File("open"));
        Assert.AreEqual(item.English, read.References!.Plan!.Items.Single().English);
        Assert.AreEqual(ListAnswer(p).Text, read.References.Plan.Proposals.Single().Raw);
        Assert.IsTrue(read.Provenance.Models.Any(m => m.Role == "video reference drawing prompt")); read.Validate();
    }
    [TestMethod]
    public async Task PauseReusesCompletedPromptAndResumesOnlyUnfinishedRequest()
    {
        using var files = new VideoPreparationTests.TestFiles(); var previous = ApplicationBackgroundOperations.Current;
        var controller = new BackgroundOperationController(new(files.File("background.json"))); ApplicationBackgroundOperations.Current = controller;
        try
        {
            var p = Root(); var a = VideoReferencePlans.Add(p); a.Name = "Cat"; var b = VideoReferencePlans.Add(p); b.Name = "Workshop";
            var batch = new VideoReferenceBatch(Guid.NewGuid().ToString("N"), p.Id, "project.lopataAIVid", [Request(p, a), Request(p, b)]);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var calls = new List<string>();
            var assistant = new VideoConceptTests.FakeAssistant { Run = async (r, token) => {
                calls.Add(r.MaterialId); if (calls.Count == 2) { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); } return PromptAnswer(); } };
            var work = VideoReferenceWork.RunAsync(batch, "test", assistant, new(files.File("receipts")),
                (r, answer) => { VideoReferencePlans.Apply(p, r, answer); return Task.CompletedTask; }, default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); await controller.PauseAsync();
            Assert.AreEqual(VideoReferenceWork.Kind, controller.State!.Kind); Assert.HasCount(1, a.History);
            await controller.ResumeAsync(default); await work.WaitAsync(TimeSpan.FromSeconds(10));
            CollectionAssert.AreEqual(new[] { a.Id, b.Id, b.Id }, calls.ToArray()); Assert.IsTrue(VideoReferencePlans.Ready(p));
        }
        finally { ApplicationBackgroundOperations.Current = previous; }
    }
    [TestMethod]
    public async Task CommitFailureReusesReceiptWithoutInferenceAndCanceledReplyIsNotSaved()
    {
        using var files = new VideoPreparationTests.TestFiles(); var p = Root(); var item = VideoReferencePlans.Add(p); item.Name = "Cat";
        var request = Request(p, item); var batch = new VideoReferenceBatch(Guid.NewGuid().ToString("N"), p.Id, request.ProjectPath, [request]);
        var assistant = new VideoConceptTests.FakeAssistant { Run = (_, _) => Task.FromResult(PromptAnswer()) }; var receipts = new VideoConceptReceiptStore(files.File("receipts"));
        await Assert.ThrowsExactlyAsync<IOException>(() => VideoReferenceWork.RunAsync(batch, "test", assistant, receipts, (_, _) => throw new IOException("disk"), default));
        await VideoReferenceWork.RunAsync(batch, "test", assistant, receipts, (r, a) => { VideoReferencePlans.Apply(p, r, a); return Task.CompletedTask; }, default);
        Assert.AreEqual(1, assistant.Calls);
        using var cancel = new CancellationTokenSource(); var second = request with { Id = Guid.NewGuid().ToString("N") };
        assistant.Run = (_, _) => { cancel.Cancel(); return Task.FromResult(PromptAnswer()); };
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => VideoReferenceWork.RunAsync(batch with { Requests = [second] }, "test", assistant, receipts, (_, _) => Task.CompletedTask, cancel.Token));
        Assert.IsNull(receipts.Read(second));
    }
    [TestMethod]
    public void InvalidArchivesAndCrossProjectBatchesAreRejected()
    {
        var p = Root(); var item = VideoReferencePlans.Add(p); item.Name = "Cat";
        var request = Request(p, item); var batch = new VideoReferenceBatch(Guid.NewGuid().ToString("N"), p.Id, request.ProjectPath, [request]);
        VideoReferenceWork.Validate(batch);
        Assert.ThrowsExactly<InvalidDataException>(() => VideoReferenceWork.Validate(batch with { Requests = [request with { ProjectId = Guid.NewGuid().ToString("N") }] }));
        Assert.ThrowsExactly<InvalidDataException>(() => VideoReferenceWork.Validate(batch with { Requests = [request, request] }));
        p.References!.Plan!.Items.Add(item); Assert.ThrowsExactly<InvalidDataException>(p.Validate);
    }
    [TestMethod]
    public void DiscoveryAtCapacityCanKeepExistingItemsButCannotPartiallyAppend()
    {
        var p = Root(); var request = Request(p); var answer = ListAnswer(p);
        VideoReferencePlans.Apply(p, request, answer);
        for (var n = 1; n < 128; n++) VideoReferencePlans.Add(p).Name = "Manual " + n;
        VideoReferencePlans.Apply(p, request with { Id = Guid.NewGuid().ToString("N") }, answer);
        Assert.HasCount(128, p.References!.Plan!.Items);
        var changed = answer with { Text = JsonSerializer.Serialize(new { references = new[] {
            new { name = "Other", cardId = p.World!.Cards[0].Id, purpose = "appearance", brief = "Another reference" } } }) };
        Assert.ThrowsExactly<InvalidDataException>(() => VideoReferencePlans.Apply(p, request with { Id = Guid.NewGuid().ToString("N") }, changed));
        Assert.HasCount(128, p.References.Plan.Items); p.Validate();
    }
}
