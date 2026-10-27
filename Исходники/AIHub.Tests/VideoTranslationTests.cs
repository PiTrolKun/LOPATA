using System.IO;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.VideoPreparation;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoTranslationTests
{
    internal static VideoConceptRequest Request(VideoPreparationProject p, string path = "translation.lopataAIVid")
    {
        VideoConceptHistory.Capture(p); VideoTranslationHistory.Capture(p);
        return new(Guid.NewGuid().ToString("N"), p.Id, path, p.IdeaRevisionId, p.IdeaText, p.Form, "core", "model.gguf", 1,
            VideoTranslationProtocol.Version, EngineSetSelection.ActiveId, VideoTranslationProtocol.Purpose, p.TranslationRevisionId, p.EnglishText);
    }
    private static VideoConceptAnswer Answer(string text = "A foggy town. No monsters.") => new(text, "", "", VideoConceptTests.Model with { Role = "video concept translation" });

    [TestMethod]
    public void EnglishEditOriginalReplyUndoAndProvenanceSurvivePortableArchive()
    {
        using var files = new VideoPreparationTests.TestFiles();
        var session = new VideoPreparationSession(files.File("recovery")); var p = session.Project;
        p.Form = "story"; p.IdeaText = "Город в тумане. Без монстров."; var original = p.IdeaText;
        var request = Request(p); var answer = Answer();
        Assert.IsTrue(VideoTranslationHistory.Apply(p, request, answer));
        p.EnglishText = "An abandoned town in fog. No monsters."; session.Changed(); session.Save(files.File("portable.lopataAIVid"));
        Assert.AreEqual(original, p.IdeaText); Assert.HasCount(2, p.TranslationHistory);
        var loaded = VideoPreparationArchive.Load(session.ArchivePath!, files.File("opened"));
        Assert.IsTrue(VideoTranslationHistory.IsCurrent(loaded)); Assert.AreEqual(p.EnglishText, loaded.EnglishText);
        Assert.IsTrue(VideoTranslationHistory.Move(loaded, -1)); Assert.AreEqual(answer.Text, loaded.EnglishText);
        Assert.IsTrue(VideoTranslationHistory.Move(loaded, 1)); Assert.AreEqual(p.EnglishText, loaded.EnglishText);
        Assert.AreEqual("video concept translation", loaded.Provenance.Models.Single().Role);
        Assert.IsFalse(VideoTranslationHistory.Apply(loaded, request, answer)); Assert.HasCount(2, loaded.TranslationHistory);
    }

    [TestMethod]
    public void SourceChangesMarkStaleAndLateRepliesNeverOverwriteEnglishEdits()
    {
        var p = new VideoPreparationProject { Form = "story", IdeaText = "Исходник" };
        var r = Request(p); p.EnglishText = "My manual English";
        Assert.IsFalse(VideoTranslationHistory.Apply(p, r, Answer())); Assert.AreEqual("My manual English", p.EnglishText);
        Assert.IsTrue(VideoTranslationHistory.Move(p, 1)); Assert.AreEqual(Answer().Text, p.EnglishText);
        p.IdeaText = "Другой исходник"; VideoConceptHistory.Capture(p);
        Assert.IsFalse(VideoTranslationHistory.IsCurrent(p));
        VideoTranslationHistory.ConfirmCurrentSource(p); Assert.IsTrue(VideoTranslationHistory.IsCurrent(p));
        Assert.AreEqual("user", VideoTranslationHistory.Selected(p)!.Source);
        p.Form = "imagery"; Assert.IsFalse(VideoTranslationHistory.IsCurrent(p));
    }

    [TestMethod]
    public void AutomaticRetranslationPreservesAnExistingManualVersion()
    {
        var p = new VideoPreparationProject { Form = "story", IdeaText = "Исходник" };
        VideoTranslationHistory.Apply(p, Request(p), Answer());
        p.EnglishText = "Manual version"; VideoTranslationHistory.Capture(p);
        p.IdeaText = "Обновлённый исходник";
        var request = Request(p) with { ActivateResult = false };
        Assert.IsFalse(VideoTranslationHistory.Apply(p, request, Answer("New translation")));
        Assert.AreEqual("Manual version", p.EnglishText); Assert.IsFalse(VideoTranslationHistory.IsCurrent(p));
        Assert.IsTrue(VideoTranslationHistory.Move(p, 1)); Assert.AreEqual("New translation", p.EnglishText);
        Assert.IsTrue(VideoTranslationHistory.IsCurrent(p));
    }

    [TestMethod]
    public void ExplicitEnglishOriginalAndOldArchivesNeedNoModel()
    {
        var p = JsonSerializer.Deserialize<VideoPreparationProject>("{\"Form\":\"story\",\"IdeaText\":\"A traveller. Лопата and LOPATA.\"}")!;
        p.Validate(); Assert.HasCount(0, p.TranslationHistory);
        VideoTranslationHistory.UseOriginal(p); p.Validate();
        Assert.AreEqual(p.IdeaText, p.EnglishText); Assert.IsTrue(VideoTranslationHistory.IsCurrent(p));
        Assert.AreEqual("original", p.TranslationHistory.Single().Source); Assert.IsNull(p.TranslationHistory.Single().Model);
        VideoTranslationHistory.UseOriginal(p); Assert.HasCount(1, p.TranslationHistory);
        p.TranslationHistory.Add(p.TranslationHistory.Single());
        Assert.ThrowsExactly<InvalidDataException>(p.Validate);
    }

    [TestMethod]
    public void TranslationContractRejectsIncompleteEmptyDuplicateAndUnexpectedFields()
    {
        string Envelope(string content, string finish = "stop") => JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = finish, message = new { content } } } });
        var valid = "{\"translation\":\"Лопата and LOPATA.\",\"changes\":\"\",\"clarifications\":\"\"}";
        Assert.AreEqual("Лопата and LOPATA.", VideoTranslationProtocol.Parse(Envelope(valid), VideoConceptTests.Model).Text);
        foreach (var bad in new[] { valid.Replace("Лопата and LOPATA.", " "), valid.Replace("\"changes\":\"\"", "\"translation\":\"again\""), valid.Replace("translation", "idea") })
            Assert.ThrowsExactly<InvalidDataException>(() => VideoTranslationProtocol.Parse(Envelope(bad), VideoConceptTests.Model));
        Assert.ThrowsExactly<InvalidDataException>(() => VideoTranslationProtocol.Parse(Envelope(valid, "length"), VideoConceptTests.Model));
        Assert.IsFalse(VideoTranslationProtocol.Instructions.Contains("УРОВЕНЬ", StringComparison.Ordinal));
        StringAssert.Contains(VideoTranslationProtocol.Instructions, "Лопата и LOPATA");
    }

    [TestMethod]
    public async Task DurableTranslationReceiptReplaysWithoutRegenerationAfterCommitFailure()
    {
        using var files = new VideoPreparationTests.TestFiles(); var p = new VideoPreparationProject { Form = "story", IdeaText = "Туман" };
        var request = Request(p); var assistant = new VideoConceptTests.FakeAssistant { Run = (_, _) => Task.FromResult(Answer()) };
        var receipts = new VideoConceptReceiptStore(files.File("receipts"));
        await Assert.ThrowsExactlyAsync<IOException>(() => VideoConceptWork.RunAsync(request, "test", assistant, receipts,
            _ => throw new IOException("locked"), default));
        await VideoConceptWork.RunAsync(request, "test", assistant, receipts,
            answer => { VideoTranslationHistory.Apply(p, request, answer); return Task.CompletedTask; }, default);
        Assert.AreEqual(1, assistant.Calls); Assert.HasCount(1, p.TranslationHistory);
        Assert.AreEqual(VideoTranslationProtocol.Kind, VideoConceptWork.KindFor(request));
    }

    [TestMethod]
    public async Task SharedTranslationPauseResumeKeepsSourceAndProducesOneCompleteVersion()
    {
        using var files = new VideoPreparationTests.TestFiles(); var previous = ApplicationBackgroundOperations.Current;
        var controller = new BackgroundOperationController(new(files.File("background.json"))); ApplicationBackgroundOperations.Current = controller;
        try
        {
            var p = new VideoPreparationProject { Form = "story", IdeaText = "Исходник" }; var request = Request(p);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var assistant = new VideoConceptTests.FakeAssistant();
            assistant.Run = async (_, token) =>
            { if (assistant.Calls == 1) { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); } return Answer(); };
            var task = VideoConceptWork.RunAsync(request, "test", assistant, new(files.File("receipts")),
                answer => { VideoTranslationHistory.Apply(p, request, answer); return Task.CompletedTask; }, default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.AreEqual(VideoTranslationProtocol.Kind, controller.State!.Kind);
            await controller.PauseAsync(); Assert.HasCount(0, p.TranslationHistory); Assert.AreEqual("Исходник", p.IdeaText);
            await controller.ResumeAsync(default); await task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.HasCount(1, p.TranslationHistory); Assert.AreEqual(Answer().Text, p.EnglishText);
        }
        finally { ApplicationBackgroundOperations.Current = previous; }
    }
}
