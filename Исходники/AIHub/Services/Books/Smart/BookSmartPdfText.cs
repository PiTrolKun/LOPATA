namespace AIHub.Services.Books.Smart;

/// <summary>For a digital PDF the model supplies region order; words come only from the original text layer.</summary>
internal static class BookSmartPdfText
{
    internal static BookSmartPage Preserve(BookContent content, int part, BookSmartPage page)
    {
        if (!content.ExactPageWords.TryGetValue(part, out var words) || words.Length == 0) return page;
        var used = new HashSet<int>(); var blocks = new List<BookSmartBlock>(); var issues = page.Issues.ToList();
        foreach (var block in page.Blocks)
        {
            if (block.Bounds is not { Length: 4 } box || box.Any(x => !double.IsFinite(x) || x < 0 || x > 1)) continue;
            var included = words.Select((word, id) => (word, id)).Where(item => !used.Contains(item.id)
                && (item.word.Left + item.word.Right) / 2 >= box[0] && (item.word.Left + item.word.Right) / 2 <= box[2]
                && (item.word.Top + item.word.Bottom) / 2 >= box[1] && (item.word.Top + item.word.Bottom) / 2 <= box[3]).ToArray();
            if (included.Length == 0) continue;
            foreach (var item in included) used.Add(item.id);
            // Region text is exact native PDF text. No OCR spelling/corrections can enter this branch.
            blocks.Add(block with { Id = blocks.Count, Text = string.Join(" ", included.Select(item => item.word.Text)), Html = null });
        }
        var remaining = words.Where((_, index) => !used.Contains(index)).ToArray();
        if (remaining.Length > 0)
        {
            blocks.Add(new(blocks.Count, string.Join(" ", remaining.Select(word => word.Text)), Questionable: true));
            issues.Add("Native PDF text outside recognized layout regions was retained; check its reading order.");
            content.Warnings.Add("BookConverter.Smart.NativePdfOrder");
        }
        // Cropped assets' block IDs refer to upstream blocks. Keep the full original page when rebasing native text.
        return page with { Blocks = blocks.ToArray(), Issues = issues.ToArray(), Assets = [] };
    }
}
