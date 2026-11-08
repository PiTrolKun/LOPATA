using System.Text;
using AIHub.Services.OfficeReader;

namespace AIHub.Controls.OfficeReader;

internal readonly record struct ReaderTextPosition(int Page, int Offset) : IComparable<ReaderTextPosition>
{
    public int CompareTo(ReaderTextPosition other) => Page != other.Page ? Page.CompareTo(other.Page) : Offset.CompareTo(other.Offset);
}

internal sealed class ReaderTextSelection
{
    private ReaderTextPosition? _anchor, _caret;
    internal event Action? Changed;
    internal Action? CopyRequested { get; set; }
    internal Action? SelectAllRequested { get; set; }
    internal bool HasSelection => _anchor is { } anchor && _caret is { } caret && anchor != caret;
    internal void Clear() { _anchor = _caret = null; Changed?.Invoke(); }
    internal void Begin(int page, int offset, bool extend)
    {
        var position = new ReaderTextPosition(page, offset);
        if (!extend || _anchor is null) _anchor = position;
        _caret = position; Changed?.Invoke();
    }
    internal void Extend(int page, int offset) { if (_anchor is null) return; _caret = new(page, offset); Changed?.Invoke(); }
    internal void SelectAll(int pages)
    { if (pages > 0) { _anchor = new(0, 0); _caret = new(pages - 1, int.MaxValue); Changed?.Invoke(); } }
    internal (ReaderTextPosition Start, ReaderTextPosition End)? Bounds => _anchor is { } a && _caret is { } c
        ? a.CompareTo(c) <= 0 ? (a, c) : (c, a) : null;

    internal (int Start, int Length) Range(int page, int length)
    {
        if (Bounds is not { } range || page < range.Start.Page || page > range.End.Page) return (0, 0);
        var start = page == range.Start.Page ? Math.Clamp(range.Start.Offset, 0, length) : 0;
        var end = page == range.End.Page ? Math.Clamp(range.End.Offset, 0, length) : length;
        return (start, Math.Max(0, end - start));
    }

    internal async Task<string> ReadAsync(ReaderDocument document, CancellationToken token)
    {
        if (Bounds is not { } bounds) return "";
        var builder = new StringBuilder();
        for (var page = bounds.Start.Page; page <= Math.Min(bounds.End.Page, document.Pages.Count - 1); page++)
        {
            token.ThrowIfCancellationRequested();
            var text = (await document.ReadTextAsync(page, token)).Text;
            var start = page == bounds.Start.Page ? Math.Clamp(bounds.Start.Offset, 0, text.Length) : 0;
            var end = page == bounds.End.Page ? Math.Clamp(bounds.End.Offset, 0, text.Length) : text.Length;
            if (builder.Length > 0) builder.AppendLine();
            builder.Append(text.AsSpan(start, Math.Max(0, end - start)));
        }
        return builder.ToString();
    }
}
