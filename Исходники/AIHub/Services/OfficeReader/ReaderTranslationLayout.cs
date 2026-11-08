using System.IO;
using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;

namespace AIHub.Services.OfficeReader;

internal sealed record ReaderTranslationArea(string Id, string Text, double Left, double Top, double Width, double Height,
    double Size, string Font, bool Translate, uint Color = 0xff000000);
internal sealed record ReaderTranslationPage(int Index, double Width, double Height, string Image, ReaderTranslationArea[] Areas);
internal sealed record ReaderTranslationLayout(string Pdf, ReaderTranslationPage[] Pages, ReaderTranslationPiece[] Pieces);

internal static class ReaderTranslationLayoutReader
{
    internal static async Task<ReaderTranslationLayout> ReadAsync(ReaderDocument document, ReaderSpeechRange range, string directory, CancellationToken token)
    {
        if (document.PdfPath is null) throw new InvalidDataException("Reader.Translation.NoText");
        if (range.FirstPage < 0 || range.Count < 1 || range.Count > document.Pages.Count - range.FirstPage) throw new ArgumentOutOfRangeException(nameof(range));
        var pages = new List<ReaderTranslationPage>(); var pieces = new List<ReaderTranslationPiece>();
        for (var index = range.FirstPage; index < range.FirstPage + range.Count; index++)
        {
            var page = await Task.Run(() => Read(document.PdfPath, index, range, token), token);
            if (!page.Areas.Any(a => a.Translate)) continue;
            var image = Path.Combine(directory, $"page-{index}.png");
            // Copy the renderer output: page-cache eviction may occur during a long translation.
            var rendered = await document.Backend.RenderAsync(index, 2200, token);
            File.Copy(rendered, image, true);
            pages.Add(page with { Image = image });
            foreach (var area in page.Areas.Where(a => a.Translate))
            {
                var parts = ChatSpeechText.Parts(area.Text, stripMarkup: false);
                for (var part = 0; part < parts.Count; part++) pieces.Add(new(area.Id + ":" + part, parts[part]));
            }
        }
        if (pieces.Count == 0) throw new InvalidDataException("Reader.Translation.NoText");
        return new(document.PdfPath, pages.ToArray(), pieces.ToArray());
    }

    internal static ReaderTranslationPage Read(string path, int index, ReaderSpeechRange range, CancellationToken token)
    {
        using var pdf = PdfDocument.Open(path); var page = pdf.GetPage(index + 1);
        var blocks = UnsupervisedReadingOrderDetector.Instance.Get(DocstrumBoundingBoxes.Instance.GetBlocks(page.GetWords().ToArray()));
        var areas = new List<ReaderTranslationArea>(); var position = 0; var lineNumber = 0;
        var start = index == range.FirstPage ? range.FirstOffset : 0;
        var end = index == range.FirstPage + range.Count - 1 ? range.LastOffset : int.MaxValue;
        foreach (var block in blocks)
        foreach (var line in block.TextLines)
        {
            token.ThrowIfCancellationRequested(); if (lineNumber > 0) position += Environment.NewLine.Length;
            var text = new StringBuilder(); var letters = new List<(int Offset, UglyToad.PdfPig.Content.Letter Letter)>();
            foreach (var word in line.Words)
            {
                if (text.Length > 0) text.Append(' ');
                foreach (var letter in word.Letters) { letters.Add((text.Length, letter)); text.Append(letter.Value); }
            }
            var source = text.ToString(); var from = Math.Clamp(start - position, 0, source.Length); var to = Math.Clamp(end - (long)position, 0, source.Length);
            void Add(int first, int last, bool translate)
            {
                if (last <= first || string.IsNullOrWhiteSpace(source[first..last])) return;
                var glyphs = letters.Where(l => l.Offset < last && l.Offset + l.Letter.Value.Length > first).Select(l => l.Letter).ToArray();
                if (glyphs.Length == 0) return;
                var left = glyphs.Min(l => l.BoundingBox.Left); var right = glyphs.Max(l => l.BoundingBox.Right);
                var top = page.Height - glyphs.Max(l => l.BoundingBox.Top); var bottom = page.Height - glyphs.Min(l => l.BoundingBox.Bottom);
                var size = glyphs.Select(l => l.PointSize).Order().ElementAt(glyphs.Length / 2);
                var rgb = glyphs[0].Color.ToRGBValues();
                var color = 0xff000000u | ((uint)Math.Clamp(rgb.Item1 * 255, 0, 255) << 16)
                    | ((uint)Math.Clamp(rgb.Item2 * 255, 0, 255) << 8) | (uint)Math.Clamp(rgb.Item3 * 255, 0, 255);
                areas.Add(new($"p{index}l{lineNumber}c{first}", source[first..last], left, top, Math.Max(1, right - left), Math.Max(1, bottom - top), size,
                    glyphs[0].FontName ?? "Arial", translate, color));
            }
            Add(0, from, false); Add(from, (int)Math.Max(from, to), true); Add((int)Math.Max(from, to), source.Length, false);
            position += source.Length; lineNumber++;
        }
        return new(index, page.Width, page.Height, "", areas.ToArray());
    }
}
