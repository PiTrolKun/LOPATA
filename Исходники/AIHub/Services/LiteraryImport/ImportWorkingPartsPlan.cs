using System.Globalization;
using System.IO;

namespace AIHub.Services.LiteraryImport;

public sealed record ImportWorkingPart(string Id, int Number, int Chapter, string Title, int Start, int Length, bool ParagraphSplit);

/// <summary>A partition of the confirmed manuscript, never a rewritten copy.</summary>
public sealed class ImportWorkingPartsPlan
{
    public int Version { get; set; } = 1;
    public string ProjectId { get; set; } = "";
    public string BookRevision { get; set; } = "";
    public string Mode { get; set; } = "manual";
    public int Selected { get; set; }
    public List<ImportWorkingPart> Parts { get; set; } = [];
    public int Warnings => Parts.Count(p => p.ParagraphSplit);

    public static ImportWorkingPartsPlan Create(ImportReviewBook book, string mode)
    {
        book.Validate();
        if (string.IsNullOrWhiteSpace(book.Text)) throw new InvalidDataException("Empty book.");
        var plan = new ImportWorkingPartsPlan { ProjectId = book.ProjectId, BookRevision = ImportSession.Hash(book.Text), Mode = mode };
        var boundaries = StringInfo.ParseCombiningCharacters(book.Text).ToHashSet(); boundaries.Add(book.Text.Length);
        var headings = book.Headings.Where(h => h.Length > 0 && boundaries.Contains(h.Start)).OrderBy(h => h.Start)
            .DistinctBy(h => h.Start).ToArray();
        var starts = headings.Select(h => h.Start).Prepend(0).Distinct().Order().ToArray();
        for (var chapter = 0; chapter < starts.Length; chapter++)
        {
            var start = starts[chapter]; var end = chapter + 1 < starts.Length ? starts[chapter + 1] : book.Text.Length;
            var heading = headings.FirstOrDefault(h => h.Start == start);
            var title = heading is null ? "" : book.HeadingTitle(heading);
            while (start < end)
            {
                var cut = Math.Min(end, start + LiteraryModelPolicy.DraftCharacters);
                if (cut < end)
                {
                    var newline = book.Text.LastIndexOf('\n', cut - 1, cut - start);
                    // Do not strand a short heading before an oversized first paragraph.
                    if (newline >= start + (cut - start) / 2) cut = newline + 1;
                    else
                    {
                        var space = book.Text.LastIndexOf(' ', cut - 1, cut - start);
                        if (space > start + (cut - start) / 2) cut = space + 1;
                    }
                }
                while (cut > start && !boundaries.Contains(cut)) cut--;
                if (cut == start) throw new InvalidDataException("Text element exceeds working part capacity.");
                plan.Parts.Add(plan.Part(book.Text, chapter + 1, title, start, cut, cut < end)); start = cut;
            }
        }
        plan.Validate(book); return plan;
    }

    private ImportWorkingPart Part(string text, int chapter, string title, int start, int end, bool internalBoundary)
        => new(ImportSession.Hash(ProjectId + BookRevision + ":" + start + ":" + end)[..32].ToLowerInvariant(),
            Parts.Count + 1, chapter, title, start, end - start,
            internalBoundary && text[end - 1] is not ('\n' or '\r'));

    public void MoveBoundary(ImportReviewBook book, int index, int relativeOffset)
    {
        Validate(book);
        if (index < 0 || index >= Parts.Count - 1) throw new ArgumentOutOfRangeException(nameof(index));
        var left = Parts[index]; var right = Parts[index + 1]; var end = right.Start + right.Length;
        var cut = (long)left.Start + relativeOffset;
        if (left.Chapter != right.Chapter || cut <= left.Start || cut >= end
            || relativeOffset > LiteraryModelPolicy.DraftCharacters || end - cut > LiteraryModelPolicy.DraftCharacters
            || !StringInfo.ParseCombiningCharacters(book.Text).Contains((int)cut))
            throw new InvalidDataException("Invalid working part boundary.");
        Parts[index] = Part(book.Text, left.Chapter, left.Title, left.Start, (int)cut, true) with { Number = left.Number };
        Parts[index + 1] = Part(book.Text, right.Chapter, right.Title, (int)cut, end,
            index + 2 < Parts.Count && Parts[index + 2].Chapter == right.Chapter) with { Number = right.Number };
        Validate(book);
    }

    public void Validate(ImportReviewBook book)
    {
        book.Validate();
        if (Version != 1 || ProjectId != book.ProjectId || BookRevision != ImportSession.Hash(book.Text)
            || Mode is not ("auto" or "manual") || Parts is null || Parts.Count == 0
            || Parts.Select(p => p.Id).Distinct().Count() != Parts.Count || Selected < 0 || Selected >= Parts.Count)
            throw new InvalidDataException("Stale or invalid working part plan.");
        var boundaries = StringInfo.ParseCombiningCharacters(book.Text).ToHashSet(); boundaries.Add(book.Text.Length);
        var position = 0; var number = 0;
        foreach (var part in Parts)
        {
            if (part.Start != position || part.Length <= 0 || part.Length > LiteraryModelPolicy.DraftCharacters
                || (long)part.Start + part.Length > book.Text.Length || part.Number != ++number || part.Chapter < 1
                || part.Title is null || !Guid.TryParseExact(part.Id, "N", out _) || !boundaries.Contains(part.Start + part.Length))
                throw new InvalidDataException("Working parts do not cover the manuscript.");
            position += part.Length;
        }
        if (position != book.Text.Length) throw new InvalidDataException("Missing manuscript ending.");
    }
}
