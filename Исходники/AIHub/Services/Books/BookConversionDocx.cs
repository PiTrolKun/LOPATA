using System.IO;
using System.Text;
using System.Xml.Linq;
using AngleSharp.Dom;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using W = DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace AIHub.Services.Books;

internal static class BookConversionDocx
{
    public static BookContent Read(string path, CancellationToken ct)
    {
        // Package opened read-only: relationship access cannot modify the source.
        using var document = WordprocessingDocument.Open(path, false);
        var main = document.MainDocumentPart ?? throw new InvalidDataException("BookConverter.Error.InvalidDocx");
        var result = new BookContent { Title = document.PackageProperties.Title ?? Path.GetFileNameWithoutExtension(path), Author = document.PackageProperties.Creator ?? "", Description = document.PackageProperties.Description ?? "", Language = document.PackageProperties.Language ?? "und" };
        var images = new Dictionary<string, string>();
        void LoadImages(OpenXmlPart owner)
        {
            foreach (var relation in owner.Parts)
            {
                if (relation.OpenXmlPart is not ImagePart image) continue;
                var key = "image" + result.Assets.Count + (image.ContentType switch { "image/png" => ".png", "image/jpeg" => ".jpg", "image/gif" => ".gif", "image/svg+xml" => ".svg", _ => Path.GetExtension(image.Uri.ToString()) });
                using var stream = image.GetStream(); using var buffer = new MemoryStream(); stream.CopyTo(buffer); result.Assets[key] = buffer.ToArray(); images[owner.Uri + ":" + relation.RelationshipId] = key;
            }
        }
        LoadImages(main);
        if (main.FootnotesPart is { } f) LoadImages(f);
        if (main.EndnotesPart is { } e) LoadImages(e);
        string Render(OpenXmlElement node, OpenXmlPart owner)
        {
            ct.ThrowIfCancellationRequested();
            if (node is W.Text text) return BookMarkup.Escape(text.Text);
            if (node is W.Break) return "<br/>";
            if (node is W.TabChar) return "&#9;";
            if (node is W.FootnoteReference fn) return "<a href=\"#note" + fn.Id?.Value + "\"><sup>" + fn.Id?.Value + "</sup></a>";
            if (node is W.EndnoteReference en) return "<a href=\"#endnote" + en.Id?.Value + "\"><sup>" + en.Id?.Value + "</sup></a>";
            if (node is A.Blip image) return image.Embed?.Value is { } id && images.TryGetValue(owner.Uri + ":" + id, out var key) ? "<img src=\"" + key + "\"/>" : "";
            if (node is W.Hyperlink hyperlink)
            {
                var url = hyperlink.Anchor?.Value is { } anchor ? "#" + anchor : owner.HyperlinkRelationships.FirstOrDefault(r => r.Id == hyperlink.Id?.Value)?.Uri.ToString();
                return "<a href=\"" + BookMarkup.Escape(url) + "\">" + string.Concat(node.ChildElements.Select(c => Render(c, owner))) + "</a>";
            }
            if (node is W.BookmarkStart bookmark) return "<span id=\"" + BookMarkup.Escape(bookmark.Name?.Value) + "\"></span>";
            var inner = string.Concat(node.ChildElements.Where(x => x is not W.ParagraphProperties && x is not W.RunProperties && x is not W.TableProperties && x is not W.TableCellProperties).Select(c => Render(c, owner)));
            if (node is W.Run run)
            {
                if (run.RunProperties?.Bold is not null) inner = "<strong>" + inner + "</strong>";
                if (run.RunProperties?.Italic is not null) inner = "<em>" + inner + "</em>";
                if (run.RunProperties?.Strike is not null) inner = "<s>" + inner + "</s>";
            }
            var tag = node switch { W.Paragraph p when p.ParagraphProperties?.ParagraphStyleId?.Val?.Value?.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) == true => "h2", W.Paragraph => "p", W.Table => "table", W.TableRow => "tr", W.TableCell => "td", _ => "" };
            return tag.Length == 0 ? inner : "<" + tag + ">" + inner + "</" + tag + ">";
        }
        var content = main.Document?.Body ?? throw new InvalidDataException("BookConverter.Error.InvalidDocx");
        var body = string.Concat(content.ChildElements.Select(c => Render(c, main)));
        if (main.FootnotesPart?.Footnotes is { } footnotes)
            foreach (var note in footnotes.Elements<W.Footnote>().Where(x => x.Id?.Value > 0)) body += "<section id=\"note" + note.Id!.Value + "\">" + Render(note, main.FootnotesPart!) + "</section>";
        if (main.EndnotesPart?.Endnotes is { } endnotes)
            foreach (var note in endnotes.Elements<W.Endnote>().Where(x => x.Id?.Value > 0)) body += "<section id=\"endnote" + note.Id!.Value + "\">" + Render(note, main.EndnotesPart!) + "</section>";
        result.Parts.Add(new BookPart(result.Title, body));
        result.Warnings.Add("BookConverter.Warning.DocxImport");
        return result;
    }
    public static void Write(BookContent book, string path)
    {
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        doc.PackageProperties.Title = book.Title; doc.PackageProperties.Creator = book.Author; doc.PackageProperties.Description = book.Description; doc.PackageProperties.Language = book.Language;
        var main = doc.AddMainDocumentPart(); main.Document = new W.Document(new W.Body()); var body = main.Document.Body!;
        var imageIndex = 0u;
        W.Run Image(IElement image)
        {
            var key = image.GetAttribute("src") ?? "";
            if (!book.Assets.TryGetValue(key, out var bytes)) throw new InvalidDataException("BookConverter.Error.ImageUnavailable");
            var part = main.AddImagePart(BookMarkup.MediaType(key)); using (var output = part.GetStream(FileMode.Create)) output.Write(bytes);
            using var bitmap = SkiaSharp.SKBitmap.Decode(bytes);
            var cx = 5_400_000L; var cy = bitmap is null ? 3_600_000L : (long)(cx * (double)bitmap.Height / bitmap.Width);
            var id = ++imageIndex;
            return new W.Run(new W.Drawing(new DW.Inline(new DW.Extent { Cx = cx, Cy = cy }, new DW.DocProperties { Id = id, Name = "Image " + id }, new A.Graphic(new A.GraphicData(new PIC.Picture(new PIC.NonVisualPictureProperties(new PIC.NonVisualDrawingProperties { Id = id, Name = key }, new PIC.NonVisualPictureDrawingProperties()), new PIC.BlipFill(new A.Blip { Embed = main.GetIdOfPart(part) }, new A.Stretch(new A.FillRectangle())), new PIC.ShapeProperties(new A.Transform2D(new A.Offset { X = 0, Y = 0 }, new A.Extents { Cx = cx, Cy = cy }), new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }))) { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))));
        }
        IEnumerable<OpenXmlElement> Inline(INode node, bool bold = false, bool italic = false)
        {
            if (node is IText t) { var properties = new W.RunProperties(); if (bold) properties.Append(new W.Bold()); if (italic) properties.Append(new W.Italic()); return [new W.Run(properties, new W.Text(t.Data) { Space = SpaceProcessingModeValues.Preserve })]; }
            if (node is not IElement e) return [];
            if (e.LocalName is "style" or "script") return [];
            if (e.LocalName == "img") return [Image(e)];
            if (e.LocalName == "br") return [new W.Run(new W.Break())];
            var children = e.ChildNodes.SelectMany(n => Inline(n, bold || e.LocalName is "strong" or "b", italic || e.LocalName is "em" or "i")).ToArray();
            if (e.LocalName == "a" && e.GetAttribute("href") is { } href)
            {
                var link = new W.Hyperlink(children);
                if (href.StartsWith('#')) link.Anchor = Anchor(href[1..]);
                else if (Uri.TryCreate(href, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto") link.Id = main.AddHyperlinkRelationship(uri, true).Id;
                return [link];
            }
            return children;
        }
        var bookmarkId = 0;
        IEnumerable<OpenXmlElement> Blocks(IElement element)
        {
            if (element.LocalName is "style" or "script") return [];
            if (element.LocalName == "table")
            {
                var rows = element.QuerySelectorAll("tr").Where(row => row.ParentElement?.Closest("table") == element).ToArray();
                var columns = rows.Select(r => r.Children.Count(e => e.LocalName is "td" or "th")).DefaultIfEmpty(1).Max();
                var table = new W.Table(new W.TableProperties(), new W.TableGrid(Enumerable.Range(0, columns).Select(_ => new W.GridColumn())));
                foreach (var row in rows)
                {
                    var tr = new W.TableRow();
                    foreach (var cell in row.Children.Where(e => e.LocalName is "td" or "th"))
                    {
                        var tableCell = new W.TableCell(Blocks(cell));
                        if (tableCell.LastChild is not W.Paragraph) tableCell.Append(new W.Paragraph());
                        tr.Append(tableCell);
                    }
                    table.Append(tr);
                }
                return [table];
            }
            if (element.Children.Any(e => e.LocalName is "p" or "section" or "div" or "h1" or "h2" or "h3" or "table" or "blockquote"))
            {
                var blocks = new List<OpenXmlElement>();
                if (!string.IsNullOrEmpty(element.Id)) blocks.Add(Bookmark(element.Id));
                foreach (var child in element.ChildNodes)
                    if (child is IElement el) blocks.AddRange(Blocks(el));
                    else if (child.TextContent.Trim().Length > 0) blocks.Add(new W.Paragraph(Inline(child)));
                return blocks;
            }
            var p = new W.Paragraph();
            if (element.LocalName is "h1" or "h2" or "h3") p.ParagraphProperties = new W.ParagraphProperties(new W.ParagraphStyleId { Val = "Heading1" });
            if (!string.IsNullOrEmpty(element.Id)) { var id = (++bookmarkId).ToString(); p.Append(new W.BookmarkStart { Id = id, Name = Anchor(element.Id) }); p.Append(element.ChildNodes.SelectMany(n => Inline(n))); p.Append(new W.BookmarkEnd { Id = id }); }
            else p.Append(Inline(element));
            return [p];
        }
        W.Paragraph Bookmark(string name) { var id = (++bookmarkId).ToString(); return new W.Paragraph(new W.BookmarkStart { Id = id, Name = Anchor(name) }, new W.BookmarkEnd { Id = id }); }
        foreach (var part in book.Parts)
            foreach (var child in BookMarkup.Parse(part.Html).Body!.ChildNodes)
                if (child is IElement e) body.Append(Blocks(e)); else if (child.TextContent.Trim().Length > 0) body.Append(new W.Paragraph(Inline(child)));
        main.Document.Save();
        book.Warnings.Add("BookConverter.Warning.DocxNotes");
    }
    private static string Anchor(string name) => "b_" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..24];
}
