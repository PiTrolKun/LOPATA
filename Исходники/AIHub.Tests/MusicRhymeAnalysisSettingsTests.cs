using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class MusicRhymeAnalysisSettingsTests
{
    [TestMethod]
    public void FragmentCountShareAndMinimumBudgetControlActualSelection()
    {
        var source = PairGroups(6, false);
        Assert.AreEqual(1, Select(source).Fragments.Length);
        Assert.AreEqual(4, Select(source, ("analysis.line_share", 1), ("analysis.max_fragments", 4)).Fragments.Length);
        Assert.AreEqual(1, Select(source, ("analysis.line_share", 1), ("analysis.max_fragments", 1)).Fragments.Length);
        Assert.AreEqual(3, Select(source, ("analysis.line_share", .01), ("analysis.minimum_lines", 6)).Fragments.Length);
    }

    [TestMethod]
    public void FirstFragmentMayExceedTheBudgetOnlyWhenEnabled()
    {
        var source = PairGroups(1, false);
        Assert.AreEqual(1, Select(source, ("analysis.line_share", .01), ("analysis.minimum_lines", 1)).Fragments.Length);
        Assert.AreEqual(0, Select(source, ("analysis.line_share", .01), ("analysis.minimum_lines", 1),
            ("analysis.first_over_budget", 0)).Fragments.Length);
    }

    [TestMethod]
    public void GoodTextCanOfferSeveralSeparateVariationsWhenRequested()
    {
        var source = PairGroups(4, true);
        Assert.AreEqual(1, Select(source, ("analysis.line_share", 1)).Fragments.Length);
        var expanded = Select(source, ("analysis.line_share", 1), ("analysis.variation_fragments", 2));
        Assert.AreEqual(2, expanded.Fragments.Length);
        Assert.IsTrue(expanded.Fragments.All(fragment => fragment.Kind == "variation"));
        Assert.AreEqual(4, expanded.Fragments.SelectMany(fragment => fragment.Lines).Distinct().Count());
    }

    [TestMethod]
    public void MaximumFragmentWidthConstrainsCoherentSentenceWindows()
    {
        var source = Analysis([8, 8, 8], [0, 0, 0], ["Начало фразы,", "её середина,", "и конец предложения."]);
        Assert.AreEqual(3, Select(source).Fragments.Single().Lines.Length);
        var shorter = Select(source, ("analysis.max_fragment_lines", 2));
        Assert.IsTrue(shorter.Fragments.Length > 0);
        Assert.IsTrue(shorter.Fragments.All(fragment => fragment.Lines.Length <= 2));
    }

    [TestMethod]
    public void LengthDifferenceReferenceCountAndSupportAffectTheRankedOpportunity()
    {
        var source = Analysis([8, 8, 8, 10], [0, 0, 0, 0]);
        Assert.IsFalse(Select(source).Fragments.Any(fragment => fragment.Kind == "length-opportunity"));
        Assert.IsTrue(Select(source, ("analysis.length_difference", 2)).Fragments.Any(fragment => fragment.Kind == "length-opportunity"));
        Assert.IsFalse(Select(source, ("analysis.length_difference", 2), ("analysis.reference_lines", 4))
            .Fragments.Any(fragment => fragment.Kind == "length-opportunity"));
        Assert.IsFalse(Select(source, ("analysis.length_difference", 2), ("analysis.reference_share", .8))
            .Fragments.Any(fragment => fragment.Kind == "length-opportunity"));
    }

    [TestMethod]
    public void AlternationToleranceAndMinimumLengthControlItsProtection()
    {
        var source = Analysis([8, 8, 8, 11], [0, 0, 0, 0]);
        Assert.IsTrue(Select(source).Fragments.Any(fragment => fragment.Kind == "length-opportunity"));
        Assert.IsFalse(Select(source, ("analysis.alternation_tolerance", 3))
            .Fragments.Any(fragment => fragment.Kind == "length-opportunity"));
        Assert.IsTrue(Select(source, ("analysis.alternation_tolerance", 3), ("analysis.alternation_min_lines", 6))
            .Fragments.Any(fragment => fragment.Kind == "length-opportunity"));
    }

    [TestMethod]
    public void StrongAnchorKeyLengthChangesWhetherMismatchIsProven()
    {
        var source = Analysis([8, 8, 8, 8], [0, 0, 0, 0]);
        var keys = new[] { "а", "а", "н", "т" };
        source = source with { Lines = source.Lines.Select((line, index) => line with {
            Readings = [new(line.Ending, "ru", keys[index], 1, line.Ending)]
        }).ToArray(), Issues = [new(3, "Rhyme", 4), new(4, "Rhyme", 3)] };
        Assert.IsFalse(Select(source).Fragments.Any(fragment => fragment.Kind == "mismatch"));
        Assert.IsTrue(Select(source, ("analysis.strong_key_units", 1)).Fragments.Any(fragment => fragment.Kind == "mismatch"));
    }

    [TestMethod]
    public void EditorialUsesTheSavedRequestSnapshotRatherThanNewDefaults()
    {
        var analysis = PairGroups(4, false);
        var length = analysis.Lines.Max(line => line.Start + line.Length);
        var step = new MusicRhymeStep { Request = new(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
            new string(' ', length), 0, length, "core", null, "", "ru") {
                Tuning = Tuning(("analysis.line_share", 1), ("analysis.max_fragments", 4))
            }, Analysis = analysis with { SelectionVersion = MusicRhymeSelection.Version } };
        Assert.AreEqual(4, MusicRhymeEditorial.ForScope(step).Fragments.Length);
    }

    private static MusicRhymeAnalysis PairGroups(int count, bool known)
    {
        var result = Analysis(Enumerable.Repeat(8, count * 2).ToArray(),
            Enumerable.Range(0, count * 2).Select(index => index / 2).ToArray());
        return known ? result with { Lines = result.Lines.Select(line => line with {
            Readings = [new(line.Ending, "ru", "от", 1, line.Ending)]
        }).ToArray() } : result;
    }

    private static MusicRhymeAnalysis Analysis(int[] syllables, int[] stanzas, string[]? texts = null)
    {
        var offset = 0;
        var lines = syllables.Select((count, index) => {
            var ending = "конец" + (char)('а' + index);
            var text = texts?[index] ?? "Я читаю строку про " + ending + ".";
            var line = new MusicRhymeLine(index + 1, offset, text.Length, stanzas[index], text, ending,
                offset, "ru", count, []);
            offset += text.Length + 1;
            return line;
        }).ToArray();
        return new(lines, []);
    }

    private static MusicRhymeAnalysis Select(MusicRhymeAnalysis analysis, params (string Id, double Value)[] values) =>
        MusicRhymeSelection.Select(analysis, 0, analysis.Lines.Max(line => line.Start + line.Length), tuning: Tuning(values));

    private static MusicRhymeTuning Tuning(params (string Id, double Value)[] values) =>
        new() { Values = values.ToDictionary(item => item.Id, item => item.Value, StringComparer.Ordinal) };
}
