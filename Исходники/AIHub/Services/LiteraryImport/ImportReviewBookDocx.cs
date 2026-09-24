using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Wordprocessing;

namespace AIHub.Services.LiteraryImport;

internal static class ImportReviewBookDocx
{
    public static void Append(Body body, ImportReviewBook book, CancellationToken ct)
    {
        var headings = book.Headings;
        var marks = book.Marks;
        var boundaries = marks.SelectMany(m => new[] { m.Start, m.Start + m.Length })
            .Concat(headings.SelectMany(h => new[] { h.Start, h.Start + h.Length }))
            .Append(0).Append(book.Text.Length).Distinct().Order().ToArray();
        var paragraph = new Paragraph(); body.Append(paragraph);
        for (var i = 0; i < boundaries.Length - 1; i++)
        {
            ct.ThrowIfCancellationRequested();
            var start = boundaries[i]; var end = boundaries[i + 1];
            var doubt = marks.Any(m => start >= m.Start && start < m.Start + m.Length);
            var heading = headings.Any(h => start >= h.Start && start < h.Start + h.Length);
            var lines = book.Text[start..end].Split('\n');
            for (var line = 0; line < lines.Length; line++)
            {
                if (line > 0) { paragraph = new Paragraph(); body.Append(paragraph); }
                var properties = new RunProperties();
                if (heading) properties.Append(new Bold());
                if (doubt) properties.Append(new DocumentFormat.OpenXml.Wordprocessing.Color { Val = "8B2500" }, new Highlight { Val = HighlightColorValues.Yellow });
                var run = new Run(properties, new Text(lines[line].TrimEnd('\r')) { Space = SpaceProcessingModeValues.Preserve });
                paragraph.Append(run);
            }
        }
    }
}
