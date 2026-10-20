using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIHub.Services.Books;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using SkiaSharp;
using Image = System.Windows.Controls.Image;
using FontFamily = System.Windows.Media.FontFamily;

namespace AIHub.Controls;

/// <summary>Inert structural reading view: no browser, script execution, remote fetches or external navigation.</summary>
internal static class BookReviewFlowRenderer
{
    public static FlowDocument Create(BookReviewDocument book, Func<string, string> localize)
    {
        var result = new FlowDocument { FontFamily = new FontFamily("Segoe UI"), FontSize = 16,
            PagePadding = new Thickness(28), ColumnWidth = double.PositiveInfinity, LineHeight = 25 };
        result.SetResourceReference(FlowDocument.ForegroundProperty, "TextPrimaryBrush");
        var html = new HtmlParser().ParseDocument(book.GetHtml());
        if (html.Body is not null) AddBlocks(result.Blocks, html.Body.ChildNodes, book, localize);
        return result;
    }

    public static FlowDocument Create(BookReviewDocument book, Func<string, string> localize,
        BookReviewEvidence evidence, bool isCurrent, out int unresolved)
    {
        var result = Create(book, localize);
        unresolved = BookReviewHighlightLayer.Apply(result, evidence, isCurrent, localize);
        return result;
    }

    private static void AddBlocks(BlockCollection target, IEnumerable<INode> nodes, BookReviewDocument book, Func<string, string> l)
    {
        Paragraph? run = null;
        foreach (var node in nodes)
        {
            if (node is IText text)
            {
                if (string.IsNullOrWhiteSpace(text.TextContent)) continue;
                if (run is null) { run = Paragraph(); target.Add(run); }
                run.Inlines.Add(new Run(text.TextContent)); continue;
            }
            if (node is not IElement element || element.LocalName is "script" or "style" or "head" or "iframe" or "object" or "embed") continue;
            var tag = element.LocalName;
            if (tag is "table") { target.Add(Table(element, book, l)); run = null; continue; }
            if (tag is "ul" or "ol")
            {
                var list = new System.Windows.Documents.List { MarkerStyle = tag == "ol" ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc, Margin = new Thickness(18, 4, 0, 10) };
                foreach (var item in element.Children.Where(e => e.LocalName == "li"))
                { var row = new ListItem(); AddBlocks(row.Blocks, item.ChildNodes, book, l); if (row.Blocks.Count == 0) row.Blocks.Add(Paragraph()); list.ListItems.Add(row); }
                target.Add(list); run = null; continue;
            }
            if (tag is "section" or "article" or "div" or "main" or "body" or "blockquote")
            { AddBlocks(target, element.ChildNodes, book, l); run = null; continue; }
            if (tag is "p" or "pre" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "figure" or "figcaption")
            {
                var paragraph = Paragraph();
                if (tag.StartsWith('h') && tag.Length == 2)
                { paragraph.FontSize = tag == "h1" ? 27 : tag == "h2" ? 23 : 20; paragraph.FontWeight = FontWeights.SemiBold; paragraph.Margin = new Thickness(0, 18, 0, 10); }
                if (tag == "pre") paragraph.FontFamily = new FontFamily("Consolas");
                AddInlines(paragraph.Inlines, element.ChildNodes, book, l);
                target.Add(paragraph); run = null; continue;
            }
            if (tag == "hr") { target.Add(new Paragraph(new Run("────────────"))); run = null; continue; }
            if (run is null) { run = Paragraph(); target.Add(run); }
            AddInlines(run.Inlines, [element], book, l);
        }
    }

    private static Paragraph Paragraph() => new() { Margin = new Thickness(0, 0, 0, 10) };

    private static Table Table(IElement element, BookReviewDocument book, Func<string, string> l)
    {
        var result = new Table { CellSpacing = 0, Margin = new Thickness(0, 8, 0, 14) };
        var group = new TableRowGroup(); result.RowGroups.Add(group);
        var rows = element.QuerySelectorAll("tr").Where(row => row.Closest("table") == element).ToArray();
        foreach (var source in rows)
        {
            var row = new TableRow(); group.Rows.Add(row);
            foreach (var cell in source.Children.Where(c => c.LocalName is "td" or "th"))
            {
                var output = new TableCell { BorderThickness = new Thickness(.5), Padding = new Thickness(7) };
                output.SetResourceReference(TableCell.BorderBrushProperty, "LineBrush");
                if (cell.LocalName == "th") output.FontWeight = FontWeights.SemiBold;
                if (int.TryParse(cell.GetAttribute("colspan"), out var columns) && columns is > 0 and <= 1000) output.ColumnSpan = columns;
                if (int.TryParse(cell.GetAttribute("rowspan"), out var lines) && lines is > 0 and <= 1000) output.RowSpan = lines;
                AddBlocks(output.Blocks, cell.ChildNodes, book, l);
                if (output.Blocks.Count == 0) output.Blocks.Add(Paragraph());
                row.Cells.Add(output);
            }
        }
        return result;
    }

    private static void AddInlines(InlineCollection target, IEnumerable<INode> nodes, BookReviewDocument book, Func<string, string> l)
    {
        foreach (var node in nodes)
        {
            if (node is IText text) { target.Add(new Run(text.TextContent)); continue; }
            if (node is not IElement element || element.LocalName is "script" or "style" or "iframe" or "object" or "embed") continue;
            if (element.LocalName == "br") { target.Add(new LineBreak()); continue; }
            if (element.LocalName is "svg" or "image")
            {
                var embedded = element.LocalName == "image" ? new[] { element } : element.QuerySelectorAll("*").Where(child => child.LocalName == "image").ToArray();
                var shown = false;
                foreach (var child in embedded)
                {
                    var href = child.Attributes.FirstOrDefault(attribute => attribute.LocalName == "href")?.Value;
                    var image = Image(href, book);
                    if (image is null) continue;
                    target.Add(new InlineUIContainer(image)); shown = true;
                }
                if (!shown) target.Add(new Run("[" + l("BookConverter.Review.ImageUnavailable") + "]"));
                continue;
            }
            if (element.LocalName == "img")
            {
                var image = Image(element.GetAttribute("src"), book);
                if (image is null) target.Add(new Run("[" + l("BookConverter.Review.ImageUnavailable") + "]"));
                else target.Add(new InlineUIContainer(image));
                continue;
            }
            Span span = element.LocalName switch { "b" or "strong" => new Bold(), "i" or "em" => new Italic(), "u" => new Underline(), _ => new Span() };
            if (element.LocalName is "sub" or "sup") { span.BaselineAlignment = element.LocalName == "sup" ? BaselineAlignment.Superscript : BaselineAlignment.Subscript; span.FontSize = 12; }
            if (element.LocalName == "a")
            {
                // Deliberately use a Span, never a navigating Hyperlink.
                span.TextDecorations = TextDecorations.Underline;
                span.ToolTip = element.GetAttribute("href");
            }
            AddInlines(span.Inlines, element.ChildNodes, book, l); target.Add(span);
        }
    }

    private static Image? Image(string? source, BookReviewDocument book)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(source)) return null;
            byte[] bytes;
            if (source.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                var comma = source.IndexOf(',');
                if (comma < 0 || !source[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase)) return null;
                bytes = Convert.FromBase64String(source[(comma + 1)..]);
            }
            else if (!book.Assets.TryGetValue(source, out bytes!)) return null;
            using var bitmap = SKBitmap.Decode(bytes);
            if (bitmap is null) return null;
            using var png = SKImage.FromBitmap(bitmap); using var encoded = png.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = new MemoryStream(encoded.ToArray());
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = Math.Min(1000, bitmap.Width); image.StreamSource = stream; image.EndInit(); image.Freeze();
            return new Image { Source = image, Stretch = Stretch.Uniform, MaxWidth = 760, MaxHeight = 700,
                Width = Math.Min(760, bitmap.Width), Margin = new Thickness(0, 8, 0, 8) };
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or IOException or NotSupportedException) { return null; }
    }
}
