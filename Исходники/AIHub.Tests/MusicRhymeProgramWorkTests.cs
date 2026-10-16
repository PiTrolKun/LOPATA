using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicRhymeProgramWorkTests
{
    [TestMethod]
    public async Task CancelledAutomaticReviewDoesNotPublishAStaleStepOrAccessTheCorpus()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var published = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(() => MusicRhymeProgramWork.RunAsync(Step(),
            step => { published++; return step; }, Progress(), cancellation.Token));
        Assert.AreEqual(0, published);
    }

    [TestMethod]
    public void OldStepsKeepGenerationUnrequestedUntilTheModelPipelineIsExplicitlyStarted()
    {
        var step = Step();
        var old = JsonSerializer.Serialize(new { step.Request, step.Original, step.ProgramReady, step.Stage });
        var restored = JsonSerializer.Deserialize<MusicRhymeStep>(old)!;
        Assert.IsFalse(restored.GenerationRequested);
        Assert.IsFalse(restored.Snapshot().GenerationRequested);
    }

    [TestMethod]
    public async Task EnteringTheModelPipelineMarksEvenAnAlreadyCompletedLegacyStep()
    {
        var previous = ApplicationBackgroundOperations.Current;
        try
        {
            ApplicationBackgroundOperations.Current = null;
            var step = Step() with { ProgramReady = true, ModelReady = true, Stage = "Ready", Model = "saved model text" };
            var result = await new MusicRhymeWork(() => throw new InvalidOperationException("No model should start"))
                .RunAsync(step, value => value, Progress(), CancellationToken.None);
            Assert.IsTrue(result.GenerationRequested);
            Assert.AreEqual("saved model text", result.Model);
            Assert.AreEqual("Ready", result.Stage);
        }
        finally { ApplicationBackgroundOperations.Current = previous; }
    }

    [TestMethod, TestCategory("RhymeCpuRuntime")]
    public async Task InstalledCorpusBuildsSoftwareSuggestionsWithoutConsentUiOrAHeavyLease()
    {
        if (Environment.GetEnvironmentVariable("LOPATA_RHYME_CPU_RUNTIME") != "1")
        { Assert.Inconclusive("Opt-in installed corpus and existing phonetic index test"); return; }
        var previous = ApplicationBackgroundOperations.Current;
        var confirm = ComponentLicenseGate.ConfirmAsync;
        var folder = Path.Combine(Path.GetTempPath(), "lopata-rhyme-cpu-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var popups = 0;
            ComponentLicenseGate.ConfirmAsync = (_, _) => { popups++; throw new InvalidOperationException("Automatic review must not request consent"); };
            var corpus = await MusicRhymeCorpus.VerifyInstalledAsync(Progress(), CancellationToken.None);
            using (var lexicon = new MusicRhymeLexicon(corpus, CancellationToken.None))
            {
                var index = Path.Combine(AppDataPaths.BaseDirectory, "Music", "RhymeIndexes", lexicon.Hash + "-phonetic-v1.sqlite");
                if (!File.Exists(index)) { Assert.Inconclusive("An existing phonetic index is required; the test does not build a corpus index"); return; }
            }
            var controller = new BackgroundOperationController(new BackgroundOperationStore(Path.Combine(folder, "operation.json")));
            ApplicationBackgroundOperations.Current = controller;
            using var occupied = controller.BeginAuxiliary();
            var stages = new List<string>();
            var result = await MusicRhymeProgramWork.RunAsync(Step(), step => { stages.Add(step.Stage); return step; },
                Progress(), CancellationToken.None);
            Assert.IsTrue(result.ProgramReady); Assert.IsNotNull(result.Analysis);
            Assert.AreEqual("Reference", result.Stage);
            Assert.IsFalse(result.GenerationRequested); Assert.IsFalse(result.ModelReady);
            Assert.AreEqual("", result.Model); Assert.AreEqual("", result.Reply);
            Assert.AreEqual("", result.Prompt); Assert.AreEqual("", result.Reference);
            Assert.IsTrue(stages.Contains("Candidates")); Assert.AreEqual("Reference", stages[^1]);
            Assert.AreEqual(0, popups); Assert.IsTrue(controller.IsRunning);
            Assert.IsFalse(File.Exists(Path.Combine(folder, "operation.json")));
            Assert.IsTrue(result.ProgramChanges.Length > 0, "The installed dictionary should provide a real software proposal for the fixture.");
        }
        finally
        {
            ApplicationBackgroundOperations.Current = previous; ComponentLicenseGate.ConfirmAsync = confirm;
            var full = Path.GetFullPath(folder);
            if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(full).StartsWith("lopata-rhyme-cpu-", StringComparison.Ordinal)) throw new InvalidOperationException();
            Directory.Delete(full, true);
        }
    }

    private static IProgress<RagBookProgress> Progress() => new InlineProgress<RagBookProgress>(_ => { });
    private static MusicRhymeStep Step()
    {
        const string text = "[Verse]\nЯ открываю словарь на экране,\nКаждое слово — новая игра.\nСегодня привет, завтра как дела.\nИ кот изучает слова до утра.";
        return new() { Request = new(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), text, 0, text.Length,
            "core", null, "", "ru"), Original = text };
    }
}
