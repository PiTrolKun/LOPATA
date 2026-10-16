using System.Globalization;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>One scoped editorial selection shared by display, candidates and the model task.</summary>
public static class MusicRhymeEditorial
{
    public static MusicRhymeAnalysis ForScope(MusicRhymeStep step, CancellationToken token = default)
    {
        var analysis = step.Analysis!;
        // Historical display keeps its old numbering, but a removed title must not become a lyric target.
        // Unfinished operations receive a complete current lexical analysis in MusicRhymeWork.
        if (analysis.SelectionVersion < MusicRhymeSelection.Version)
        {
            var prepared = MusicRhymeText.Prepare(step.Request.Source);
            analysis = analysis with { Lines = analysis.Lines.Where(l => l.Start >= 0 && l.Length >= 0
                && l.Start <= prepared.Length-l.Length && !string.IsNullOrWhiteSpace(prepared.Substring(l.Start,l.Length))).ToArray() };
        }
        return MusicRhymeSelection.Select(analysis, step.Request.Start, step.Request.Length, token, step.Request.Tuning);
    }

    public static MusicRhymeSuggestion[] RebaseSuggestions(MusicRhymeAnalysis? previous,
        MusicRhymeAnalysis current, MusicRhymeSuggestion[] suggestions)
    {
        if (previous is null) return suggestions;
        var old = previous.Lines.ToDictionary(l => l.Number);
        var next = current.Lines.ToDictionary(l => l.Start);
        return suggestions.Where(s => old.TryGetValue(s.Line,out var line) && next.ContainsKey(line.Start))
            .Select(s => s with { Line=next[old[s.Line].Start].Number }).ToArray();
    }

    public static IEnumerable<MusicRhymeIssue> Targets(MusicRhymeStep step) => ForScope(step).Fragments
        .SelectMany(fragment => fragment.Lines.Select(line => new MusicRhymeIssue(line, IssueKind(fragment.Kind),
            fragment.Lines.Length == 2 ? fragment.Lines.Single(n => n != line) : null))).Distinct();

    public static string Explain(MusicRhymeFragment fragment, Func<string, string> localize) =>
        string.Format(CultureInfo.CurrentUICulture, localize(fragment.ReasonKey), fragment.Arguments.Cast<object>().ToArray());

    public static HashSet<int> HighlightedLines(MusicRhymeAnalysis? analysis) => analysis is null ? []
        : analysis.SelectionVersion > 0 ? analysis.Fragments.SelectMany(f => f.Lines).ToHashSet()
        : analysis.Issues.Where(i => i.Kind is "Rhyme" or "Rhythm").Select(i => i.Line).ToHashSet();

    private static string IssueKind(string kind) => kind switch {
        "mismatch" => "Rhyme", "length-opportunity" => "LengthOpportunity",
        "uncertainty" => "Uncertain", "variation" => "Variation", _ => "Opportunity"
    };
}
