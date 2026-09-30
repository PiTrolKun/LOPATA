using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class LiteraryContextManagementTests
{
    private static LiteraryStudioState Conversation()
    {
        var state = new LiteraryStudioState { Input = "UNSENT_QUESTION", Task = "AUTHOR_TASK", Result = "MANUSCRIPT", Quotes = [new("book", "QUOTE")] };
        state.Add("User", "first decision"); state.Add("Advisor", "unaccepted proposal");
        state.Add("User", "late correction"); state.Add("Advisor", "latest reply"); return state;
    }
    [TestMethod]
    public void RecommendationsRespectBoundaryAndRejectionAndReserve()
    {
        var meter = new StudioContextMeter(500, 1512);
        Assert.AreEqual(1000, meter.Available); Assert.AreEqual(500, meter.Free);
        Assert.AreEqual(StudioContextMethod.Retelling, meter.Recommended(false));
        Assert.AreEqual(StudioContextMethod.Smart, meter.Recommended(true));
        Assert.AreEqual(StudioContextMethod.Manual, (meter with { Input = 499 }).Recommended(false));
        Assert.AreEqual(1d, (meter with { Input = 4000 }).UsedRatio);
    }
    [TestMethod]
    public void AcceptedRetellingKeepsOriginalsInputQuotesAndOrdersSummaryBeforeRecentTurns()
    {
        var state = Conversation(); var fingerprint = StudioContextPlan.Stamp(state); var plan = StudioContextPlan.Capture(state);
        var originalIds = state.Messages.Select(m => m.Id).ToArray();
        var copy = plan.Apply(state, plan.Items.Take(2).Select(i => i.Id).ToHashSet(), "DECISIONS", StudioContextMethod.Smart, 1000, 500, "ARCHIVE_ONLY");
        Assert.AreEqual(fingerprint, StudioContextPlan.Stamp(state));
        CollectionAssert.AreEqual(originalIds, copy.Messages.Take(4).Select(m => m.Id).ToArray());
        CollectionAssert.AreEqual(new[] { "DECISIONS", "late correction", "latest reply" }, StudioContextPlan.Conversation(copy).Select(m => m.Text).ToArray());
        Assert.AreEqual("UNSENT_QUESTION", copy.Input); Assert.AreEqual("MANUSCRIPT", copy.Result);
        Assert.AreEqual("QUOTE", copy.Quotes.Single().Text);
        Assert.IsFalse(StudioContextPlan.Conversation(copy).Any(m => m.Role == "ContextEvent"));
        Assert.IsFalse(LiteraryStudioContext.Contains(copy, copy.Messages.Last()));
        // Repeated smart compression must still identify the newest original turns, not the newly appended summary.
        CollectionAssert.AreEqual(new[] { "late correction", "latest reply" }, StudioContextPlan.Capture(copy).Items.TakeLast(2).Select(i => i.Text).ToArray());
    }
    [TestMethod]
    public void ManualCleaningDoesNotGenerateSummaryOrEraseJournal()
    {
        var state = Conversation(); var plan = StudioContextPlan.Capture(state);
        var copy = plan.Apply(state, new HashSet<string> { plan.Items[1].Id }, null, StudioContextMethod.Manual, null, null, "record");
        Assert.AreEqual(5, copy.Messages.Count); Assert.IsFalse(copy.Messages[1].InContext);
        Assert.IsFalse(copy.Messages.Any(m => m.Role == "Summary"));
        Assert.IsNull(copy.Messages.Last().ContextChange!.Before);
    }
    [TestMethod]
    public void ChangedOrInvalidPlanIsRejectedWithoutTouchingState()
    {
        var state = Conversation(); var plan = StudioContextPlan.Capture(state); state.Input += "changed";
        var stamp = StudioContextPlan.Stamp(state);
        Assert.Throws<InvalidOperationException>(() => plan.Apply(state, new HashSet<string> { plan.Items[0].Id }, null, StudioContextMethod.Manual, 1, 0, ""));
        Assert.AreEqual(stamp, StudioContextPlan.Stamp(state));
        plan = StudioContextPlan.Capture(state);
        Assert.Throws<InvalidOperationException>(() => plan.Apply(state, new HashSet<string> { "FOREIGN" }, "text", StudioContextMethod.Smart, 1, 0, ""));
    }
    [TestMethod]
    public void WriterCompressionChangesOnlyEffectivePromptAndExpiresOnNewTask()
    {
        var state = Conversation(); state.Transfer("LONG_TASK"); state.Action = "Rewrite"; state.Result = "TARGET"; state.RevisionRequirements.Add("LONG_RULE");
        var plan = StudioContextPlan.Capture(state);
        var copy = plan.Apply(state, new HashSet<string> { "writer/task", "writer/requirements" }, "SHORT_TASK_AND_RULE", StudioContextMethod.Smart, 50, 25, "record");
        Assert.AreEqual("LONG_TASK", copy.Task); Assert.AreEqual("TARGET", copy.Result); Assert.AreEqual("LONG_RULE", copy.RevisionRequirements.Single());
        Assert.AreEqual("SHORT_TASK_AND_RULE", StudioContextPlan.Writer(copy).Task);
        Assert.AreEqual("TARGET", StudioContextPlan.Writer(copy).Target);
        Assert.IsFalse(LiteraryStudioContext.Contains(copy, copy.Messages.Single(m => m.Role == "Task")));
        copy.Transfer("NEW_TASK"); Assert.AreEqual("NEW_TASK", StudioContextPlan.Writer(copy).Task); Assert.IsNull(copy.WriterContext);
    }
    [TestMethod]
    public void AcceptedSummaryPersistsAndConcurrentSaveCannotOverwriteAnotherWindow()
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-context-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var project = new LiteraryProject(); File.WriteAllText(Path.Combine(root, "project.json"), JsonSerializer.Serialize(project)); new LiteraryProjectLayout(root).Initialize();
            var store = new LiteraryStudioStore(new(root)); var state = store.Load(); state.Add("User", "original"); store.Save(state);
            var plan = StudioContextPlan.Capture(state); var candidate = plan.Apply(state, new HashSet<string> { plan.Items[0].Id }, "summary", StudioContextMethod.Retelling, 10, 5, "record");
            store.Save(candidate); var other = new LiteraryStudioStore(new(root)); var loaded = other.Load();
            Assert.AreEqual("original", loaded.Messages[0].Text); Assert.AreEqual("summary", StudioContextPlan.Conversation(loaded).Single().Text);
            Assert.AreEqual(StudioContextMethod.Retelling, loaded.Messages.Last().ContextChange!.Method);
            other.Save(loaded); // Same content isn't a conflict; changed content is.
            loaded.Input = "OTHER_WINDOW"; other.Save(loaded);
            Assert.Throws<IOException>(() => store.Save(candidate));
            Assert.AreEqual("OTHER_WINDOW", new LiteraryStudioStore(new(root)).Load().Input);
        }
        finally { Directory.Delete(root, true); }
    }
    [TestMethod]
    public async Task BoundedPassesReadEveryCharacterAndMergeChronologically()
    {
        var state = Conversation(); state.Messages[0].Text = string.Concat(Enumerable.Repeat("😀LONG_WITHOUT_SPACES", 170)) + "\n\nLAST_CORRECTION";
        var plan = StudioContextPlan.Capture(state); var reading = new List<string>(); var merging = new List<string>();
        var engine = Engine(3000, messages =>
        {
            var user = messages.Last().Content; var source = user[(user.IndexOf("\n\n", StringComparison.Ordinal) + 2)..];
            if (user.StartsWith("Перескажи")) { reading.Add(source); return "PART_" + reading.Count; }
            merging.Add(source); return "merged decisions";
        });
        var result = await engine.RunAsync(plan, StudioContextMethod.Retelling, .5, null, CancellationToken.None);
        var original = string.Join("\n\n", plan.Items.Select(i => $"[{i.Id}] {i.Role}\n{i.Text}"));
        Assert.AreEqual(original, string.Concat(reading)); Assert.IsTrue(reading.Count > 1);
        Assert.IsTrue(merging.Count > 0); StringAssert.Contains(merging[0], "PART_1\n\nPART_2");
        Assert.IsTrue(result.AfterTokens <= result.BeforeTokens / 2); Assert.AreEqual(plan.Items.Count, result.ReplacedIds.Count);
    }
    [TestMethod]
    public async Task SmartCompressionRetainsRecentTurnsAndStrongerPassReadsOriginalAgain()
    {
        var state = Conversation(); var plan = StudioContextPlan.Capture(state); var inputs = new List<string>();
        var engine = Engine(12000, messages => { inputs.Add(messages.Last().Content); return "short"; });
        var result = await engine.RunAsync(plan, StudioContextMethod.Smart, .5, null, CancellationToken.None);
        Assert.AreEqual(2, result.ReplacedIds.Count); Assert.IsFalse(result.ReplacedIds.Contains(plan.Items.Last().Id));
        var originalInput = inputs.First(); inputs.Clear();
        await engine.RunAsync(plan, StudioContextMethod.Smart, .35, null, CancellationToken.None);
        StringAssert.Contains(inputs.First(), "first decision"); Assert.AreEqual(StudioContextPlan.Stamp(state), plan.Fingerprint);
    }
    [TestMethod]
    public async Task CancellationOrTooSmallWindowNeverReturnsPartialAcceptance()
    {
        var plan = StudioContextPlan.Capture(Conversation()); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => Engine(3000, _ => "summary").RunAsync(plan, StudioContextMethod.Retelling, .5, null, cancellation.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Engine(128, _ => throw new Exception("Must not generate")).RunAsync(plan, StudioContextMethod.Retelling, .5, null, CancellationToken.None));
    }
    private static LiteraryContextCompaction Engine(int capacity, Func<IReadOnlyList<ImageAnalysisHiddenMessage>, string> generate) => new(
        (text, ct) => { ct.ThrowIfCancellationRequested(); return Task.FromResult(text.Length); },
        (messages, ct) => { ct.ThrowIfCancellationRequested(); return Task.FromResult(messages.Sum(m => m.Content.Length)); },
        (messages, ct) => Task.FromResult(generate(messages)), () => capacity);
}
