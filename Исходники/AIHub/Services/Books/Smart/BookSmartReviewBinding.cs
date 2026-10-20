using System.Text.RegularExpressions;

namespace AIHub.Services.Books.Smart;

internal static class BookSmartReviewBinding
{
    internal static BookReviewMark[] Bind(BookReviewDocument actualTarget, IEnumerable<BookReviewMark> marks)
    {
        var text = Normalize(string.Join("\n", actualTarget.Blocks.Select(block => block.Text)));
        return marks.Select(mark =>
        {
            var snippet = Normalize(mark.Text);
            var first = snippet.Length == 0 ? -1 : text.IndexOf(snippet, StringComparison.Ordinal);
            var unique = first >= 0 && text.IndexOf(snippet, first + snippet.Length, StringComparison.Ordinal) < 0;
            // The exported format may introduce a title or move notes. Ambiguous anchors remain visibly unresolved.
            return mark with { Occurrence = unique ? 0 : int.MaxValue };
        }).ToArray();
    }
    private static string Normalize(string text) => Regex.Replace(text, @"\s+", " ").Trim();
}
