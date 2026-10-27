using System.IO;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.VideoPreparation;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoWorldTests
{
    internal static VideoPreparationProject Root()
    {
        var p = VideoDevelopmentTests.Root(); var d = p.Development!;
        d.IdeaText = "First, the cat prepares tea. Then the filmmaker waves and says «Снято!».";
        VideoConceptHistory.Capture(d); VideoTranslationHistory.UseOriginal(d); p.CurrentStep = "world";
        VideoWorld.Ensure(p); VideoWorld.Capture(p); return p;
    }
    internal static VideoConceptAnswer Answer(string title = "Мягкая акварель") => new(JsonSerializer.Serialize(new
    {
        title, summary = "Тёплый уютный мир.", cards = new[] {
            new { kind = "general", name = "Общий образ", description = "Мягкая акварель, тёплые цвета.", @fixed = "Без монстров" },
            new { kind = "character", name = "Кот", description = "Рыжий кот в зелёном фартуке.", @fixed = "Круглые очки" } }
    }), "", "", VideoConceptTests.Model with { Role = "video visual world" });
    internal static VideoWorldBatch Batch(VideoPreparationProject p, string path = "project.lopataAIVid")
    {
        VideoWorld.Capture(p); var w = p.World!; var id = Guid.NewGuid().ToString("N");
        var requests = Enumerable.Range(1, 3).Select(level => new VideoConceptRequest(Guid.NewGuid().ToString("N"), p.Id, path,
            w.RevisionId, "", p.Form, "core", "test.gguf", level, VideoWorldProtocol.Version, EngineSetSelection.ActiveId,
            VideoWorld.Purpose, ActivateResult: false, ContextRevisionId: p.Development!.TranslationRevisionId,
            ContextText: w.Context, ContextOriginal: p.IdeaText, ResponseLanguage: "ru-RU")).ToArray();
        foreach (var r in requests) w.Options.Add(new() { Id = r.Id, BatchId = id, Freedom = r.Freedom, Context = w.Context, BaseRevisionId = w.RevisionId });
        return new(id, p.Id, path, requests);
    }
    [TestMethod]
    public async Task PauseResumeReusesFirstReceiptAndCompletesOnlyUnfinishedLevels()
    {
        using var files = new VideoPreparationTests.TestFiles(); var previous = ApplicationBackgroundOperations.Current;
        var controller = new BackgroundOperationController(new(files.File("background.json"))); ApplicationBackgroundOperations.Current = controller;
        try
        {
            var p = Root(); var batch = Batch(p); var calls = new List<int>();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var assistant = new VideoConceptTests.FakeAssistant { Run = async (r, token) =>
            { calls.Add(r.Freedom); if (calls.Count == 2) { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); } return Answer(); } };
            var task = VideoWorldWork.RunAsync(batch, "test", assistant, new(files.File("receipts")), (r, status, answer) =>
            { var o = p.World!.Options.Single(o => o.Id == r.Id); o.Status = status; o.Answer = answer; return Task.CompletedTask; }, default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); await controller.PauseAsync();
            Assert.AreEqual(VideoWorld.Kind, controller.State!.Kind); Assert.AreEqual("ready", p.World!.Options[0].Status);
            await controller.ResumeAsync(default); await task.WaitAsync(TimeSpan.FromSeconds(10));
            CollectionAssert.AreEqual(new[] { 1, 2, 2, 3 }, calls.ToArray()); Assert.IsTrue(p.World.Options.All(o => o.Status == "ready"));
        }
        finally { ApplicationBackgroundOperations.Current = previous; }
    }
    [TestMethod]
    public void EditingAnEarlierWorldVersionPreservesItsSuccessorsAndUndoReturnsToItsActualParent()
    {
        var p = Root(); var w = p.World!; var initial = w.RevisionId;
        w.Cards[0].Text.IdeaText = "First look"; VideoWorld.Capture(p); var first = w.RevisionId;
        w.Cards[0].Text.IdeaText = "Second look"; VideoWorld.Capture(p);
        Assert.IsTrue(VideoWorld.Move(p, -1)); Assert.AreEqual(first, w.RevisionId);
        w.Cards[0].Text.IdeaText = "A branch from the first look";
        Assert.IsTrue(VideoWorld.Move(p, -1)); Assert.AreEqual(first, w.RevisionId); Assert.AreEqual("First look", w.Cards[0].Text.IdeaText);
        Assert.IsTrue(VideoWorld.Move(p, 1)); Assert.AreEqual("Second look", w.Cards[0].Text.IdeaText);
        Assert.IsTrue(w.History.Any(v => v.Cards.Contains("A branch from the first look"))); Assert.AreNotEqual(initial, w.RevisionId);
    }
    [TestMethod]
    public async Task AReplyAfterCancellationCannotBecomeASelectableOption()
    {
        using var files = new VideoPreparationTests.TestFiles(); var p = Root(); var batch = Batch(p);
        using var cancel = new CancellationTokenSource(); var states = new List<string>();
        var assistant = new VideoConceptTests.FakeAssistant { Run = (_, _) => { cancel.Cancel(); return Task.FromResult(Answer()); } };
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => VideoWorldWork.RunAsync(batch, "test", assistant, new(files.File("receipts")),
            (_, status, _) => { states.Add(status); return Task.CompletedTask; }, cancel.Token));
        Assert.IsFalse(states.Contains("ready")); Assert.AreEqual("", p.World!.Cards[0].Text.IdeaText);
    }
    [TestMethod]
    public void ThreeInputsAreIndependentAndContainOnlyTheirLevel()
    {
        var p = Root(); var b = Batch(p); VideoWorldWork.Validate(b);
        Assert.HasCount(1, b.Requests.Select(VideoWorldProtocol.Input).Distinct().ToArray());
        foreach (var r in b.Requests)
        {
            var prompt = VideoWorldProtocol.Instructions(r.Freedom);
            foreach (var level in Enumerable.Range(1, 3)) Assert.AreEqual(level == r.Freedom, prompt.Contains("УРОВЕНЬ " + level));
            Assert.IsFalse(VideoWorldProtocol.Input(r).Contains("Мягкая акварель"));
        }
        var changed = b.Requests.ToArray(); changed[1] = changed[1] with { Text = "previous answer" };
        Assert.ThrowsExactly<InvalidDataException>(() => VideoWorldWork.Validate(b with { Requests = changed }));
    }
    [TestMethod]
    public async Task FirstReadyIsSelectableAndCancellationPreventsThirdCall()
    {
        using var files = new VideoPreparationTests.TestFiles(); var p = Root(); var b = Batch(p); var calls = new List<int>();
        using var cancel = new CancellationTokenSource();
        var assistant = new VideoConceptTests.FakeAssistant { Run = (r, _) => { calls.Add(r.Freedom); return Task.FromResult(Answer()); } };
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => VideoWorldWork.RunAsync(b, "test", assistant, new(files.File("receipts")), (r, status, a) =>
        {
            var option = p.World!.Options.Single(o => o.Id == r.Id); option.Status = status; option.Answer = a;
            if (status == "ready") { VideoWorld.Apply(p, option); cancel.Cancel(); }
            return Task.CompletedTask;
        }, cancel.Token));
        CollectionAssert.AreEqual(new[] { 1 }, calls.ToArray()); Assert.HasCount(2, p.World!.Cards);
        Assert.AreEqual("Кот", p.World.Cards[1].Name); Assert.AreEqual("ready", p.World.Options[0].Status);
    }
    [TestMethod]
    public async Task FailedLevelDoesNotHideOthersAndReceiptsAreReplayedWithoutInference()
    {
        using var files = new VideoPreparationTests.TestFiles(); var p = Root(); var b = Batch(p); var calls = new List<int>();
        var assistant = new VideoConceptTests.FakeAssistant { Run = (r, _) => { calls.Add(r.Freedom); return r.Freedom == 2
            ? Task.FromException<VideoConceptAnswer>(new InvalidDataException("invalid")) : Task.FromResult(Answer()); } };
        var states = new Dictionary<int, string>(); var receipts = new VideoConceptReceiptStore(files.File("receipts"));
        Task Commit(VideoConceptRequest r, string status, VideoConceptAnswer? a) { states[r.Freedom] = status; return Task.CompletedTask; }
        await VideoWorldWork.RunAsync(b, "test", assistant, receipts, Commit, default);
        Assert.AreEqual("ready", states[1]); Assert.AreEqual("failed", states[2]); Assert.AreEqual("ready", states[3]);
        var single = b with { Id = Guid.NewGuid().ToString("N"), Requests = [b.Requests[0]] };
        await VideoWorldWork.RunAsync(single, "test", assistant, receipts, Commit, default); Assert.HasCount(3, calls);
    }
    [TestMethod]
    public void OptionsDoNotChangeAcceptedCardsAndArchiveRetainsUndoTranslationAndModels()
    {
        using var files = new VideoPreparationTests.TestFiles(); var p = Root(); var w = p.World!; var original = p.IdeaText;
        var b = Batch(p); var o = w.Options[0]; o.Status = "ready"; o.Answer = Answer();
        Assert.AreEqual("", w.Cards[0].Text.IdeaText); VideoWorld.Apply(p, o); var accepted = w.RevisionId;
        VideoWorld.Apply(p, o); Assert.AreEqual(accepted, w.RevisionId);
        VideoTranslationHistory.UseOriginal(w.Working); w.Working.EnglishText += " Manual wording."; VideoDevelopment.Capture(p);
        var file = files.File("world.lopataAIVid"); VideoPreparationArchive.Save(p, file);
        var loaded = VideoPreparationArchive.Load(file, files.File("opened"));
        Assert.AreEqual(original, loaded.IdeaText); Assert.HasCount(3, loaded.World!.Options); Assert.AreEqual(accepted, loaded.World.RevisionId);
        StringAssert.EndsWith(loaded.World.Working.EnglishText, "Manual wording.");
        Assert.IsTrue(loaded.Provenance.Models.Any(m => m.Role == "video visual world"));
        Assert.IsTrue(VideoWorld.Move(loaded, -1)); Assert.AreEqual("", loaded.World.Cards[0].Text.IdeaText);
        Assert.IsTrue(VideoWorld.Move(loaded, 1)); Assert.HasCount(2, loaded.World.Cards);
    }
    [TestMethod]
    public void ChangingAnotherCardInvalidatesAnInFlightCardRequest()
    {
        var p = Root(); var w = p.World!; var target = w.Cards[0]; var other = VideoWorld.AddCard(p, "character");
        target.Text.ContextText = VideoWorld.CardContext(p, target);
        var r = new VideoConceptRequest(Guid.NewGuid().ToString("N"), p.Id, "test.lopataAIVid", target.Text.IdeaRevisionId, "", p.Form,
            "core", "test.gguf", 1, VideoWorldProtocol.Version, EngineSetSelection.ActiveId, VideoWorld.CardPurpose,
            ContextText: target.Text.ContextText, ContextOriginal: w.Context, MaterialId: target.Id);
        Assert.IsTrue(VideoWorld.RequestCurrent(p, r)); other.Text.IdeaText = "New appearance";
        Assert.IsFalse(VideoWorld.RequestCurrent(p, r));
    }
    [TestMethod]
    public void SourceChangeKeepsCardsAndRejectsOldProposalUntilAReviewedCurrentProposalExists()
    {
        var p = Root(); var w = p.World!; var b = Batch(p); var o = w.Options[0]; o.Status = "ready"; o.Answer = Answer();
        p.Development!.EnglishText += " Another ending."; VideoDevelopment.Capture(p);
        Assert.IsFalse(VideoWorld.SourceCurrent(p)); Assert.ThrowsExactly<InvalidDataException>(() => VideoWorld.Apply(p, o));
        VideoWorld.Review(p); Assert.IsTrue(VideoWorld.SourceCurrent(p));
        Assert.ThrowsExactly<InvalidDataException>(() => VideoWorld.Apply(p, o)); Assert.HasCount(1, w.Cards);
    }
    [TestMethod]
    public void SchemaRejectsMalformedAndDuplicateFieldsAndMissingGeneralCard()
    {
        var valid = Answer().Text; Assert.HasCount(2, VideoWorldProtocol.Read(valid).Cards);
        foreach (var invalid in new[] { "{}", valid.Replace("\"general\"", "\"place\""), valid.Replace("\"title\":", "\"title\":\"duplicate\",\"title\":"),
            valid.Replace("\"general\"", "\"unknown\"") }) Assert.ThrowsExactly<InvalidDataException>(() => VideoWorldProtocol.Read(invalid));
        var p = Root(); var b = Batch(p); p.World!.Options[0].Status = "ready";
        Assert.ThrowsExactly<InvalidDataException>(p.Validate);
    }
}
