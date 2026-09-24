namespace AIHub.Services.LiteraryImport;

public sealed record ReviewBookHeading(string Id, int Start, int Length);
public sealed record ReviewBookMark(string Id, int Start, int Length, string UnitId, string Reason);

/// <summary>A review manuscript is independent of the eventual working-part size limit.</summary>
public sealed class ImportReviewBook
{
    private readonly Dictionary<string, (ReviewBookHeading[] Headings, ReviewBookMark[] Marks)> _undoAnchors = [];
    public int Version { get; set; } = 1;
    public string ProjectId { get; set; } = "";
    public string BaseRevision { get; set; } = "";
    public string Text { get; set; } = "";
    public List<ReviewBookHeading> Headings { get; set; } = [];
    public List<ReviewBookMark> Marks { get; set; } = [];

    public void Validate()
    {
        if (Version != 1 || Text is null || Headings is null || Marks is null
            || Headings.Any(h => h.Start < 0 || h.Length < 0 || (long)h.Start + h.Length > Text.Length)
            || Marks.Any(m => m.Start < 0 || m.Length < 0 || (long)m.Start + m.Length > Text.Length)
            || Headings.Select(h => h.Id).Distinct().Count() != Headings.Count
            || Marks.Select(m => m.Id).Distinct().Count() != Marks.Count)
            throw new System.IO.InvalidDataException("Literary.Import.Corrupt");
    }

    public string HeadingTitle(ReviewBookHeading heading) => Text.Substring(heading.Start, heading.Length).Trim();

    // Called on each text edit, including undo/redo. Track anchors through an arbitrary replacement.
    // A removed marked passage disappears; an edited marked passage remains marked until approval.
    public void ReplaceText(string next, int? editStart = null, int removed = 0, int added = 0)
    {
        if (next == Text) return;
        _undoAnchors[LiteraryWorkIndex.Revision(Text)] = (Headings.ToArray(), Marks.ToArray());
        if (_undoAnchors.TryGetValue(LiteraryWorkIndex.Revision(next), out var restored))
        {
            Text = next; Headings = restored.Headings.ToList(); Marks = restored.Marks.ToList(); return;
        }
        if (_undoAnchors.Count > 220) _undoAnchors.Remove(_undoAnchors.Keys.First());
        var start = 0;
        while (start < Text.Length && start < next.Length && Text[start] == next[start]) start++;
        var oldEnd = Text.Length; var newEnd = next.Length;
        while (oldEnd > start && newEnd > start && Text[oldEnd - 1] == next[newEnd - 1]) { oldEnd--; newEnd--; }
        // Native editor offsets disambiguate edits in repeated prose; textual diff alone cannot.
        if (editStart is int exact && exact >= 0 && removed >= 0 && added >= 0
            && (long)exact + removed <= Text.Length && (long)exact + added <= next.Length
            && Text.Length - removed + added == next.Length
            && Text.AsSpan(0, exact).SequenceEqual(next.AsSpan(0, exact))
            && Text.AsSpan(exact + removed).SequenceEqual(next.AsSpan(exact + added)))
        { start = exact; oldEnd = exact + removed; newEnd = exact + added; }
        var delta = newEnd - oldEnd;
        int Map(int at, bool right) => at < start ? at : at >= oldEnd ? at + delta : right ? newEnd : start;
        var updated = new List<ReviewBookMark>();
        foreach (var mark in Marks)
        {
            var end = mark.Start + mark.Length;
            var from = Map(mark.Start, false); var to = Map(end, true);
            if (start == oldEnd && start == end) to = end; // insertion just after a mark is outside it
            if (to > from) updated.Add(mark with { Start = from, Length = to - from });
        }
        Marks = updated;
        Headings = Headings.Where(h => !(oldEnd > start && h.Start >= start && h.Start + h.Length <= oldEnd)
            || (h.Start == start && h.Start + h.Length == oldEnd && newEnd > start))
            .Select(h =>
        {
            var from = Map(h.Start, false); var to = Map(h.Start + h.Length, true);
            if (start == oldEnd && start == h.Start + h.Length) to = h.Start + h.Length;
            var newline = next.IndexOfAny(['\r', '\n'], from);
            if (newline >= 0) to = Math.Min(to, newline);
            return h with { Start = from, Length = Math.Max(0, to - from) };
        }).Where(h => h.Length > 0).ToList();
        Text = next;
        Validate();
    }
}
