using System.IO;
using SkiaSharp;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Writer;

namespace AIHub.Services.OfficeReader;

/// <summary>A reading copy: original page artwork, replaced text regions, searchable translated text.</summary>
internal static class ReaderTranslationPdf
{
    internal static void Write(ReaderTranslationLayout layout, IReadOnlyDictionary<string, string> translations,
        string destination, OutputModelUse[] models, CancellationToken token)
    {
        var temporary = destination + ".tmp";
        try
        {
            using var source = PdfDocument.Open(layout.Pdf);
            using var builder = new PdfDocumentBuilder();
            var facts = new OutputProvenance { Scenario = "office/reader/translation", CreatedUtc = DateTimeOffset.UtcNow,
                Models = models, ModelHistory = "recorded" };
            builder.DocumentInformation.Title = source.Information.Title;
            builder.DocumentInformation.Author = source.Information.Author;
            builder.DocumentInformation.Creator = "LOPATA " + facts.ProgramVersion;
            builder.DocumentInformation.Subject = "LOPATA.Provenance=" + facts.Json();
            var generated = new List<PdfDocument>();
            try
            {
                for (var i = 0; i < source.NumberOfPages; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var page = layout.Pages.SingleOrDefault(p => p.Index == i);
                    if (page is null) { builder.AddPage(source, i + 1); continue; }
                    using var stream = new MemoryStream();
                    using (var pdf = SKDocument.CreatePdf(stream))
                    {
                        var canvas = pdf.BeginPage((float)page.Width, (float)page.Height);
                        using var image = SKBitmap.Decode(page.Image) ?? throw new InvalidDataException("Reader.Translation.LayoutFailed");
                        canvas.DrawBitmap(image, new SKRect(0, 0, (float)page.Width, (float)page.Height));
                        foreach (var area in page.Areas)
                        {
                            token.ThrowIfCancellationRequested();
                            var text = area.Text;
                            if (area.Translate)
                            {
                                var parts = ChatSpeechText.Parts(text, stripMarkup: false);
                                text = string.Join(" ", Enumerable.Range(0, parts.Count).Select(n => translations[area.Id + ":" + n]));
                                using var cover = new SKPaint { Color = Background(image, area, page), IsAntialias = false };
                                // PDF font outlines and the system rasterizer can differ at the cap line.
                                canvas.DrawRect((float)area.Left - .7f, (float)area.Top - 3f,
                                    (float)area.Width + 1.4f, (float)area.Height + 6f, cover);
                            }
                            Draw(canvas, area, text, !area.Translate);
                        }
                        pdf.EndPage(); pdf.Close();
                    }
                    var translated = PdfDocument.Open(stream.ToArray()); generated.Add(translated);
                    builder.AddPage(translated, 1);
                }
                token.ThrowIfCancellationRequested(); File.WriteAllBytes(temporary, builder.Build());
                token.ThrowIfCancellationRequested(); File.Move(temporary, destination, true);
            }
            finally { foreach (var item in generated) item.Dispose(); }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static SKColor Background(SKBitmap image, ReaderTranslationArea area, ReaderTranslationPage page)
    {
        // Sample just outside the glyphs; a median avoids a nearby rule or descender changing the fill.
        var colors = new List<SKColor>();
        for (var i = 0; i < 9; i++)
        {
            var x = (int)((area.Left + area.Width * i / 8) * image.Width / page.Width);
            foreach (var y in new[] { area.Top - 1, area.Top + area.Height + 1 })
                colors.Add(image.GetPixel(Math.Clamp(x, 0, image.Width - 1), Math.Clamp((int)(y * image.Height / page.Height), 0, image.Height - 1)));
        }
        return colors.OrderBy(c => c.Red + c.Green + c.Blue).ElementAt(colors.Count / 2);
    }

    private static void Draw(SKCanvas canvas, ReaderTranslationArea area, string text, bool hidden)
    {
        var family = area.Font.Contains('+') ? area.Font[(area.Font.IndexOf('+') + 1)..] : area.Font;
        var style = area.Font.Contains("Bold", StringComparison.OrdinalIgnoreCase) ? SKFontStyle.Bold : SKFontStyle.Normal;
        using var typeface = SKTypeface.FromFamilyName(family, style) ?? SKTypeface.FromFamilyName("Segoe UI", style);
        using var font = new SKFont(typeface, (float)Math.Max(3, area.Size));
        if (!font.ContainsGlyphs(text))
        {
            var missing = text.EnumerateRunes().First(r => !font.ContainsGlyph(r.Value));
            using var fallback = SKFontManager.Default.MatchCharacter(missing.Value); font.Typeface = fallback;
            if (!hidden && !font.ContainsGlyphs(text)) throw new InvalidDataException("Reader.Translation.FontMissing");
            DrawFitted(canvas, area, text, hidden, font); return;
        }
        DrawFitted(canvas, area, text, hidden, font);
    }
    private static void DrawFitted(SKCanvas canvas, ReaderTranslationArea area, string text, bool hidden, SKFont font)
    {
        using var paint = new SKPaint { IsAntialias = true, Color = hidden ? new SKColor(0, 0, 0, 1) : new SKColor(area.Color) };
        // Keep each source line in its own region. Never silently clip an overflowing translation.
        var width = font.MeasureText(text, out var bounds, paint);
        var factor = Math.Min(1, area.Width / Math.Max(1, width));
        factor = Math.Min(factor, area.Height / Math.Max(1, bounds.Height));
        font.Size *= (float)factor;
        if (!hidden && font.Size < 3) throw new InvalidDataException("Reader.Translation.TooLong");
        font.MeasureText(text, out bounds, paint);
        canvas.DrawText(text, (float)area.Left - bounds.Left, (float)area.Top - bounds.Top, font, paint);
    }

    internal static async Task SaveAsync(string pdf, string destination, string original)
    {
        var target = Path.GetFullPath(destination);
        if (!Path.GetExtension(target).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
            || target.Equals(Path.GetFullPath(original), StringComparison.OrdinalIgnoreCase)
            || target.Equals(Path.GetFullPath(pdf), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Reader.Translation.SavePdf");
        Lopata.Updates.SafeUpdatePath.RejectLinks(target);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var input = File.OpenRead(pdf))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            { await input.CopyToAsync(output); await output.FlushAsync(); }
            File.Move(temporary, target, true);
            CollectionExportJournal.Record(target, "office/translation");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
