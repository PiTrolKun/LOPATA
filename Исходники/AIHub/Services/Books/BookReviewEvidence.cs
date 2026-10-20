using System.IO;
using System.Text;

namespace AIHub.Services.Books;

public enum BookReviewMarkKind { AiProcessed, Questionable }
public sealed record BookReviewMark(string Text, BookReviewMarkKind Kind, string SourcePath, string Reason = "", int? Page = null, int Occurrence = 0);
public sealed record BookReviewEvidence(string[] Sources, BookReviewMark[] Marks);

internal sealed record BookReviewResolvedMark(int Start, int Length, BookReviewMark Mark);
internal sealed record BookReviewResolution(BookReviewResolvedMark[] Marks, int Unresolved);

/// <summary>Conservative whitespace-only alignment against the actual exported text. No fuzzy word replacement.</summary>
internal static class BookReviewEvidenceResolver
{
    internal static string[] OriginalSources(BookReviewEvidence evidence) => evidence.Sources
        .Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    internal static BookReviewResolution Resolve(string actualText, BookReviewEvidence evidence, bool isCurrent)
    {
        if (!isCurrent) return new([], evidence.Marks.Length);
        var sources = OriginalSources(evidence).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var (text, positions) = Normalize(actualText);
        var resolved = new List<BookReviewResolvedMark>(); var missing = 0;
        foreach (var mark in evidence.Marks)
        {
            var (needle, _) = Normalize(mark.Text);
            if (needle.Length == 0 || mark.Occurrence < 0 || !sources.Contains(mark.SourcePath)) { missing++; continue; }
            var offset = 0; var found = -1;
            for (var occurrence = 0; occurrence <= mark.Occurrence; occurrence++)
            {
                found = text.IndexOf(needle, offset, StringComparison.Ordinal);
                if (found < 0) break;
                offset = found + needle.Length;
            }
            if (found < 0) { missing++; continue; }
            var start = positions[found]; var finish = positions[found + needle.Length - 1] + 1;
            resolved.Add(new(start, finish - start, mark));
        }
        return new(resolved.ToArray(), missing);
    }

    private static (string Text, int[] Positions) Normalize(string value)
    {
        var text = new StringBuilder(); var positions = new List<int>();
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsWhiteSpace(value[index]))
            {
                if (text.Length == 0 || text[^1] == ' ') continue;
                text.Append(' '); positions.Add(index);
            }
            else { text.Append(value[index]); positions.Add(index); }
        }
        if (text.Length > 0 && text[^1] == ' ') { text.Length--; positions.RemoveAt(positions.Count - 1); }
        return (text.ToString(), positions.ToArray());
    }
}
