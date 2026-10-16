using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicRhymeLargeSelectionTests
{
    [TestMethod]
    [DataRow(256)]
    [DataRow(257)]
    [DataRow(512)]
    public void LargeRhymeClassKeepsEquivalentConnectedProtectionWithoutQuadraticStorage(int count)
    {
        var (analysis, length) = Make(count);
        var selected = MusicRhymeSelection.Select(analysis, 0, length);
        Assert.AreEqual(1, selected.Fragments.Length);
        CollectionAssert.AreEqual(new[] { 1, 2 }, selected.Fragments.Single().Lines);
        Assert.AreEqual("variation", selected.Fragments.Single().Kind);
        Assert.AreEqual(count <= 256 ? count * (count - 1) / 2 : count - 1, selected.PreserveRhymePairs.Length);
        AssertConnected(selected, count);
    }

    [TestMethod]
    public void CompactProtectionUsesSecondAnchorForCopiesOfTheFirstEnding()
    {
        var (analysis, length) = Make(512, repeatedWords: true);
        var selected = MusicRhymeSelection.Select(analysis, 0, length);
        Assert.AreEqual(511, selected.PreserveRhymePairs.Length);
        foreach (var pair in selected.PreserveRhymePairs)
            Assert.AreNotEqual(analysis.Lines[pair[0] - 1].Ending, analysis.Lines[pair[1] - 1].Ending);
        AssertConnected(selected, 512);
    }

    [TestMethod]
    public void EqualRankWithinNarrowScopeKeepsTheFirstContextFromStableEnumeration()
    {
        var (analysis, _) = Make(3);
        analysis = analysis with { Lines = analysis.Lines.Select((line, index) => line with
            { Readings = [new(line.Ending, "en", "KEY" + index, 1, line.Ending)] }).ToArray() };
        var middle = analysis.Lines[1];
        var selected = MusicRhymeSelection.Select(analysis, middle.Start, middle.Length);
        var fragment = selected.Fragments.Single();
        CollectionAssert.AreEqual(new[] { 2 }, fragment.Lines);
        CollectionAssert.AreEqual(new[] { 1, 2 }, fragment.Context);
    }

    [TestMethod]
    public async Task CancellationInterruptsALongSelectionWithoutAccumulatingAllPairs()
    {
        var (analysis, length) = Make(8192);
        Assert.IsTrue(length <= 200_000, "The stress case stays inside the application input limit.");
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = Task.Run(() =>
        {
            started.SetResult();
            return MusicRhymeSelection.Select(analysis, 0, length, cancellation.Token);
        });
        await started.Task;
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(20));
        await Assert.ThrowsAsync<OperationCanceledException>(() => work.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static (MusicRhymeAnalysis Analysis, int Length) Make(int count, bool repeatedWords = false)
    {
        var lines = new MusicRhymeLine[count]; var start = 0;
        for (var i = 0; i < count; i++)
        {
            var word = repeatedWords ? i % 2 == 0 ? "night" : "light" : "word" + i;
            var text = "Line " + i + " " + word + ".";
            lines[i] = new(i + 1, start, text.Length, 0, text, word, start + text.LastIndexOf(word, StringComparison.Ordinal),
                "en", 8, [new(word, "en", "AY T", 1, word)]);
            start += text.Length + 1;
        }
        return (new(lines, []), start - 1);
    }

    private static void AssertConnected(MusicRhymeAnalysis analysis, int count)
    {
        var neighbors = Enumerable.Range(1, count).ToDictionary(n => n, _ => new List<int>());
        foreach (var pair in analysis.PreserveRhymePairs)
        {
            neighbors[pair[0]].Add(pair[1]); neighbors[pair[1]].Add(pair[0]);
        }
        var visited = new HashSet<int> { 1 }; var queue = new Queue<int>(); queue.Enqueue(1);
        while (queue.TryDequeue(out var line))
            foreach (var next in neighbors[line]) if (visited.Add(next)) queue.Enqueue(next);
        Assert.AreEqual(count, visited.Count, "All members retain the original shared rhyme through the compact constraints.");
    }
}
