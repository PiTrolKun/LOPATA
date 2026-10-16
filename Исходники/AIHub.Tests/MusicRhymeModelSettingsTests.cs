using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class MusicRhymeModelSettingsTests
{
    [TestMethod]
    public void ReserveHonoursBothConfiguredLimitsAndTheActualContext()
    {
        Assert.AreEqual(8192, MusicRhymeModelSettings.Reserve(new(), 32768));
        Assert.AreEqual(2048, MusicRhymeModelSettings.Reserve(new(), 4096));
        var settings = Tuning(("ReplyTokens", 1000), ("ReplyFraction", .25));
        Assert.AreEqual(1000, MusicRhymeModelSettings.Reserve(settings, 8192));
        Assert.AreEqual(512, MusicRhymeModelSettings.Reserve(settings, 2048));
    }

    [TestMethod]
    public void SamplingKeepsTheSelectedModelProfileUnlessExplicitlyOverridden()
    {
        var profile = MusicPoetryProfileCatalog.All.First();
        var automatic = MusicRhymeModelSettings.Sampling(Tuning(("TopK", 123)), profile);
        Assert.AreEqual(profile.TopK, automatic.TopK);
        Assert.AreEqual(profile.TopP, automatic.TopP);
        var custom = MusicRhymeModelSettings.Sampling(Tuning(("SamplingOverrides", 1), ("TopK", 123),
            ("TopP", .81), ("MinP", .12), ("RepeatPenalty", 1.3)), profile);
        Assert.AreEqual((.81, 123, .12, 1.3), custom);
    }

    [TestMethod]
    public void ReasoningAutoKeepsModelSpecificBudgetAndCustomBudgetStaysRequestLocal()
    {
        foreach (var (id, budget) in new[] { ("core", 2048), ("profile-gemma-poetry", 4096) })
        {
            var candidate = MusicPoetryModelCatalog.All.Single(item => item.Id == id);
            var model = new DebugModelInfo { Path = candidate.FileName, Name = candidate.FileName, Format = candidate.Format };
            Assert.AreEqual(budget, MusicRhymeModelSettings.ReasoningOptions(model, new()).Budget);
            Assert.AreEqual(512, MusicRhymeModelSettings.ReasoningOptions(model, Tuning(("ReasoningBudget", 512))).Budget);
            Assert.IsFalse(MusicRhymeModelSettings.ReasoningOptions(model, Tuning(("Reasoning", 0))).Enabled);
            Assert.AreEqual(budget, MusicPoetryReasoning.Resolve(model).Budget);
        }
    }

    [TestMethod]
    public void PromptAppliesReferenceAndCandidateLimitsFromTheRequestSnapshot()
    {
        var step = Step();
        step = step with { Request = step.Request with { Tuning = Tuning(("RagPromptHits", 1), ("RagPromptChars", 50), ("PromptCandidates", 1)) },
            Reference = JsonSerializer.Serialize(new { books = new[] {
                new { source = "one", text = new string('a', 100) }, new { source = "two", text = new string('b', 100) } } }),
            Suggestions = [new(1, "кот", "кот", "fixture", true), new(2, "сад", "сад", "fixture", true)] };
        using var prompt = JsonDocument.Parse(MusicRhymeTask.Prompt(step));
        var books = prompt.RootElement.GetProperty("reference").GetProperty("books");
        Assert.AreEqual(1, books.GetArrayLength());
        Assert.AreEqual(50, books[0].GetProperty("text").GetString()!.Length);
        Assert.AreEqual(1, prompt.RootElement.GetProperty("candidates").GetArrayLength());
    }

    [TestMethod]
    public void FragmentOnlyEditingNarrowsPermissionsWithoutChangingTheUserScope()
    {
        var step = Step();
        var whole = MusicRhymeTask.EditableLines(step);
        var fragment = step with { Request = step.Request with { Tuning = Tuning(("WholeStanzas", 0)) } };
        var selected = MusicRhymeTask.EditableLines(fragment);
        Assert.IsTrue(selected.Count > 0 && selected.Count < whole.Count);
        Assert.IsTrue(selected.IsSubsetOf(whole));
        Assert.AreEqual(step.Request.Text, fragment.Request.Text);
        using var prompt = JsonDocument.Parse(MusicRhymeTask.Prompt(fragment));
        CollectionAssert.AreEquivalent(selected.Select(line => line + 1).ToArray(),
            prompt.RootElement.GetProperty("editable_lines").EnumerateArray().Select(value => value.GetInt32()).ToArray());
    }

    [TestMethod]
    public void AnswerSizeCanBeLoweredButStructuralProtectionCannotBeDisabled()
    {
        var text = new string('a', 1001);
        MusicRhymeReply.Validate(text, text, new HashSet<int> { 0 });
        Assert.Throws<InvalidDataException>(() => MusicRhymeReply.Validate(text, text, new HashSet<int> { 0 }, Tuning(("MaxAnswerChars", 1000))));
        Assert.Throws<InvalidDataException>(() => MusicRhymeReply.Validate("[Verse]\nкот", "[Chorus]\nкот", new HashSet<int> { 0, 1 }, new()));
    }

    [TestMethod]
    public async Task LateRunnerCannotReturnACompletedAnswerAfterTheConfiguredDeadline()
    {
        using var runner = new DelayedModel(false);
        var error = await Assert.ThrowsAsync<TimeoutException>(() => MusicRhymeModelDeadline.RunAsync(runner,
            new(), "task", Tuning(("GenerationTimeoutSeconds", 1)), CancellationToken.None));
        Assert.AreEqual("Music.Rhyme.Settings.GenerationTimedOut", error.Message);
    }

    [TestMethod]
    public async Task PauseCancellationKeepsItsOwnContractInsteadOfBecomingATimeout()
    {
        using var runner = new DelayedModel(true);
        using var pause = new CancellationTokenSource();
        var running = MusicRhymeModelDeadline.RunAsync(runner, new(), "task", new(), pause.Token);
        pause.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => running);
    }

    private sealed class DelayedModel(bool cooperative) : IMusicRhymeModel
    {
        public async Task<string> RewriteAsync(DebugModelInfo model, string prompt, CancellationToken token)
        {
            await Task.Delay(1300, cooperative ? token : CancellationToken.None);
            return "late answer";
        }
        public void Dispose() { }
    }

    private static MusicRhymeTuning Tuning(params (string Id, double Value)[] items) => new() {
        Values = items.ToDictionary(item => item.Id, item => item.Value, StringComparer.Ordinal) };
    private static MusicRhymeStep Step()
    {
        const string song = "[Куплет]\nВ саду шумит весёлый ко́т,\nИ песню тихую поёт.\nНад крышей разлилась луна́,\nВ реке качается она́.";
        return new() { Request = new(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), song, 0, song.Length, "core", null, "", "ru"),
            Original = song, Reference = "{}", Analysis = MusicRhymeAnalyzer.Analyze(song, _ => []) };
    }
}
