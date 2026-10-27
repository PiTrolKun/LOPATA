using System.IO;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.VideoPreparation;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoDevelopmentTests
{
    internal static VideoPreparationProject Root(string form = "story")
    {
        var p = new VideoPreparationProject { Form = form, IdeaText = "The cat prepares tea. Then a filmmaker waves and says «Снято!»." };
        VideoTranslationHistory.UseOriginal(p); p.CurrentStep = "development";
        VideoDevelopment.Ensure(p); return p;
    }

    internal static VideoConceptRequest Request(VideoPreparationProject p, bool translation = false)
    {
        VideoDevelopment.Capture(p); var d = VideoDevelopment.Ensure(p);
        return new(Guid.NewGuid().ToString("N"), p.Id, "test.lopataAIVid", d.IdeaRevisionId, d.IdeaText, d.Form,
            "core", "test.gguf", 1, translation ? VideoTranslationProtocol.Version : VideoDevelopmentProtocol.Version,
            EngineSetSelection.ActiveId, translation ? VideoDevelopment.TranslationPurpose : VideoDevelopment.Purpose,
            d.TranslationRevisionId, d.EnglishText, ContextRevisionId: d.ContextRevisionId, ContextText: d.ContextText,
            ContextOriginal: d.ContextOriginal, ResponseLanguage: "ru-RU");
    }
    internal static VideoConceptAnswer Answer(string text = "Сначала кот готовит чай. Затем появляется оператор. В финале он говорит «Снято!».", bool translation = false)
        => new(text, "План составлен", "", VideoConceptTests.Model with { Role = translation ? "video development translation" : "video development plan" });

    [TestMethod]
    public void IndependentPlanAndTranslationSurviveArchiveWithActualModelRoles()
    {
        using var files = new VideoPreparationTests.TestFiles(); var p = Root(); var original = p.IdeaText; var working = p.EnglishText;
        var d = p.Development!; var r = Request(p);
        Assert.IsTrue(VideoConceptHistory.Apply(d, r, Answer()));
        var t = Request(p, translation: true); Assert.IsTrue(VideoTranslationHistory.Apply(d, t, Answer("First, the cat prepares tea. Then the filmmaker waves and says «Снято!».", true)));
        d.EnglishText += " Manual edit."; VideoDevelopment.Capture(p);
        var path = files.File("development.lopataAIVid"); VideoPreparationArchive.Save(p, path);
        var loaded = VideoPreparationArchive.Load(path, files.File("opened"));
        Assert.AreEqual(original, loaded.IdeaText); Assert.AreEqual(working, loaded.EnglishText); Assert.IsTrue(VideoDevelopment.SourceCurrent(loaded));
        Assert.AreEqual(d.EnglishText, loaded.Development!.EnglishText); Assert.HasCount(2, loaded.Provenance.Models);
        Assert.IsTrue(loaded.Provenance.Models.Any(m => m.Role == "video development translation"));
        Assert.IsTrue(VideoConceptHistory.Move(loaded.Development, -1)); Assert.AreEqual("", loaded.Development.IdeaText);
        Assert.IsTrue(VideoConceptHistory.Move(loaded.Development, 1)); Assert.AreEqual(Answer().Text, loaded.Development.IdeaText);
        Assert.IsTrue(VideoTranslationHistory.Move(loaded.Development, -1)); Assert.IsFalse(loaded.Development.EnglishText.EndsWith("Manual edit.", StringComparison.Ordinal));
        Assert.IsTrue(VideoTranslationHistory.Move(loaded.Development, 1)); Assert.AreEqual(d.EnglishText, loaded.Development.EnglishText);
    }

    [TestMethod]
    public void ChangedConceptRequiresReviewAndLatePlanStaysInHistory()
    {
        var p = Root(); var d = p.Development!; var r = Request(p);
        p.EnglishText += " The ending is different."; VideoDevelopment.Capture(p);
        Assert.IsFalse(VideoDevelopment.SourceCurrent(p)); Assert.IsFalse(VideoDevelopment.RequestCurrent(p, r));
        Assert.IsFalse(VideoConceptHistory.Apply(d, r with { ActivateResult = VideoDevelopment.RequestCurrent(p, r) }, Answer()));
        Assert.AreEqual("", d.IdeaText); Assert.HasCount(2, d.IdeaHistory);
        Assert.IsTrue(VideoConceptHistory.Move(d, 1)); Assert.IsFalse(VideoDevelopment.SourceCurrent(p));
        VideoDevelopment.RefreshSource(p); Assert.IsTrue(VideoDevelopment.SourceCurrent(p));
        Assert.AreEqual(Answer().Text, d.IdeaText); Assert.IsFalse(VideoTranslationHistory.IsCurrent(d));
        p.Validate();
    }

    [TestMethod]
    public void LatestExplicitAssistantAndOlderProjectsKeepTheirMeaning()
    {
        var p = new VideoPreparationProject { Form = "imagery", IdeaText = "A quiet city dissolves into fog." };
        VideoTranslationHistory.UseOriginal(p); p.LastAssistantModelId = "profile-katarau"; p.AssistantModelId = "executor";
        var d = VideoDevelopment.Ensure(p); Assert.AreEqual("profile-katarau", d.AssistantModelId);
        d.AssistantModelId = "core"; p.LastAssistantModelId = "executor";
        Assert.AreEqual("core", VideoDevelopment.Ensure(p).AssistantModelId);
        var old = JsonSerializer.Deserialize<VideoPreparationProject>("{\"Form\":\"story\",\"IdeaText\":\"Старый замысел\"}")!;
        old.Validate(); Assert.IsNull(old.Development); Assert.AreEqual("Старый замысел", old.IdeaText);
        p.Development!.Id = Guid.NewGuid().ToString("N"); Assert.ThrowsExactly<InvalidDataException>(p.Validate);
    }

    [TestMethod]
    [DataRow(1)] [DataRow(2)] [DataRow(3)]
    public void DevelopmentPromptContainsOnlySelectedFreedomAndStructuredContext(int level)
    {
        var prompt = VideoDevelopmentProtocol.Instructions(level);
        foreach (var other in Enumerable.Range(1, 3)) Assert.AreEqual(other == level, prompt.Contains("УРОВЕНЬ " + other, StringComparison.Ordinal));
        var p = Root(); var r = Request(p); var json = JsonDocument.Parse(VideoDevelopmentProtocol.Input(r));
        Assert.AreEqual(p.EnglishText, json.RootElement.GetProperty("working_concept").GetString());
        Assert.AreEqual("", json.RootElement.GetProperty("draft").GetString());
        Assert.AreEqual(VideoDevelopment.Kind, VideoConceptWork.KindFor(r));
        Assert.AreEqual(VideoDevelopment.TranslationKind, VideoConceptWork.KindFor(Request(p, true)));
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task DevelopmentAndItsTranslationUseSharedPauseResume(bool translating)
    {
        using var files = new VideoPreparationTests.TestFiles(); var previous = ApplicationBackgroundOperations.Current;
        var controller = new BackgroundOperationController(new(files.File("background.json"))); ApplicationBackgroundOperations.Current = controller;
        try
        {
            var p = Root(); var d = p.Development!; if (translating) d.IdeaText = "Сначала кот готовит чай. Затем оператор машет рукой.";
            var r = Request(p, translating); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var assistant = new VideoConceptTests.FakeAssistant();
            assistant.Run = async (_, token) =>
            { if (assistant.Calls == 1) { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); } return Answer(translating ? "First, the cat makes tea." : "Сначала кот готовит чай.", translating); };
            var task = VideoConceptWork.RunAsync(r, "test", assistant, new(files.File("receipts")), result =>
            { if (translating) VideoTranslationHistory.Apply(d, r, result); else VideoConceptHistory.Apply(d, r, result); return Task.CompletedTask; }, default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.AreEqual(VideoConceptWork.KindFor(r), controller.State!.Kind);
            await controller.PauseAsync(); Assert.IsFalse(d.IdeaHistory.Any(v => v.Source == "ai")); Assert.HasCount(0, d.TranslationHistory);
            await controller.ResumeAsync(default); await task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(2, assistant.Calls);
            Assert.AreEqual(1, translating ? d.TranslationHistory.Count : d.IdeaHistory.Count(v => v.Source == "ai"));
        }
        finally { ApplicationBackgroundOperations.Current = previous; }
    }

    [TestMethod]
    public async Task DurablePlanReceiptReplaysAfterFailedCommitWithoutRegenerating()
    {
        using var files = new VideoPreparationTests.TestFiles(); var p = Root(); var r = Request(p);
        var assistant = new VideoConceptTests.FakeAssistant { Run = (_, _) => Task.FromResult(Answer()) };
        var receipts = new VideoConceptReceiptStore(files.File("receipts"));
        await Assert.ThrowsExactlyAsync<IOException>(() => VideoConceptWork.RunAsync(r, "test", assistant, receipts,
            _ => throw new IOException("locked"), default));
        await VideoConceptWork.RunAsync(r, "test", assistant, receipts,
            answer => { VideoConceptHistory.Apply(p.Development!, r, answer); return Task.CompletedTask; }, default);
        Assert.AreEqual(1, assistant.Calls); Assert.HasCount(2, p.Development!.IdeaHistory);
    }
}
