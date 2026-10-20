using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using AngleSharp.Dom;
using SkiaSharp;

namespace AIHub.Services.Books;

internal static class BookConversionWriter
{
    public static void Write(BookContent book, string path, string format, BookConversionOptions options, CancellationToken ct, OutputProvenance? facts = null)
    {
        ct.ThrowIfCancellationRequested();
        switch (format)
        {
            case "epub": Epub(book, path, options); break;
            case "fb2": Fb2(book, path); break;
            case "docx": BookConversionDocx.Write(book, path); break;
            case "html": File.WriteAllText(path, BookMarkup.Body(book.Title, InlineAssets(book), options), new UTF8Encoding(false)); break;
            case "md": File.WriteAllText(path, "# " + book.Title + "\n\n" + InlineAssets(book), new UTF8Encoding(false)); book.Warnings.Add("BookConverter.Warning.MarkdownHtml"); break;
            case "txt": File.WriteAllText(path, PlainText(book), new UTF8Encoding(false)); book.Warnings.Add("BookConverter.Warning.TextOnly"); break;
            case "cbz": Cbz(book, path, options, ct); break;
            case "pdf": Pdf(book, path, options, ct, facts); break;
            default: throw new NotSupportedException("BookConverter.Error.ExternalConverter");
        }
    }
    private static string PlainText(BookContent book)
    {
        var sb = new StringBuilder();
        foreach (var part in book.Parts)
        {
            var doc = BookMarkup.Parse(part.Html);
            foreach (var style in doc.QuerySelectorAll("style,script")) style.Remove();
            foreach (var image in doc.QuerySelectorAll("img")) image.Replace(doc.CreateTextNode("[Изображение: " + (image.GetAttribute("alt") ?? "без подписи") + "]"));
            foreach (var el in doc.QuerySelectorAll("p,h1,h2,h3,h4,h5,h6,div,section,li,tr,blockquote,pre")) el.AppendChild(doc.CreateTextNode("\n"));
            sb.AppendLine(doc.Body!.TextContent.Trim()).AppendLine();
        }
        return sb.ToString();
    }
    internal static string InlineAssets(BookContent book)
    {
        var doc = BookMarkup.Parse(string.Join("\n", book.Parts.Select(p => p.Html)));
        foreach (var el in doc.QuerySelectorAll("img"))
        {
            var src = el.GetAttribute("src") ?? "";
            if (book.Assets.TryGetValue(src, out var bytes)) el.SetAttribute("src", "data:" + BookMarkup.MediaType(src) + ";base64," + Convert.ToBase64String(bytes));
        }
        foreach (var style in doc.QuerySelectorAll("style")) style.TextContent = InlineCssAssets(style.TextContent, book);
        foreach (var element in doc.All) if (element.GetAttribute("style") is { } css) element.SetAttribute("style", InlineCssAssets(css, book));
        return string.Join("", doc.Head!.QuerySelectorAll("style").Select(e => e.OuterHtml)) + doc.Body!.InnerHtml;
    }
    private static string InlineCssAssets(string css, BookContent book) => System.Text.RegularExpressions.Regex.Replace(css,
        @"url\(\s*['"" ]?(?<url>[^)'""]+)['"" ]?\s*\)", match =>
        {
            var key = match.Groups["url"].Value.Trim();
            return book.Assets.TryGetValue(key, out var bytes)
                ? "url('data:" + BookMarkup.MediaType(key) + ";base64," + Convert.ToBase64String(bytes) + "')" : match.Value;
        }, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static void Entry(ZipArchive zip, string name, string contents, CompressionLevel level = CompressionLevel.Optimal)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, level).Open(), new UTF8Encoding(false)); writer.Write(contents);
    }
    private static void Epub(BookContent book, string path, BookConversionOptions options)
    {
        XNamespace ns = "http://www.idpf.org/2007/opf", dc = "http://purl.org/dc/elements/1.1/", xhtml = "http://www.w3.org/1999/xhtml";
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Entry(zip, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);
        Entry(zip, "META-INF/container.xml", "<?xml version=\"1.0\"?><container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\"><rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>");
        var metadata = new XElement(ns + "metadata", new XAttribute(XNamespace.Xmlns + "dc", dc), new XElement(dc + "identifier", new XAttribute("id", "bookid"), "urn:uuid:" + Guid.NewGuid()), new XElement(dc + "title", book.Title), new XElement(dc + "language", book.Language.Length > 0 ? book.Language : "und"), new XElement(dc + "creator", book.Author), new XElement(dc + "description", book.Description), new XElement(dc + "publisher", book.Publisher), new XElement(dc + "rights", book.Rights), new XElement(ns + "meta", new XAttribute("property", "dcterms:modified"), DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")));
        foreach (var optional in metadata.Elements().Where(e => e.Name.Namespace == dc && e.Name.LocalName is "creator" or "description" or "publisher" or "rights" && string.IsNullOrWhiteSpace(e.Value)).ToArray()) optional.Remove();
        var manifest = new XElement(ns + "manifest", new XElement(ns + "item", new XAttribute("id", "text"), new XAttribute("href", "book.xhtml"), new XAttribute("media-type", "application/xhtml+xml")), new XElement(ns + "item", new XAttribute("id", "nav"), new XAttribute("href", "nav.xhtml"), new XAttribute("media-type", "application/xhtml+xml"), new XAttribute("properties", "nav")));
        foreach (var asset in book.Assets)
        {
            using var stream = zip.CreateEntry("OEBPS/" + asset.Key).Open(); stream.Write(asset.Value);
            var item = new XElement(ns + "item", new XAttribute("id", "r" + manifest.Elements().Count()), new XAttribute("href", asset.Key), new XAttribute("media-type", BookMarkup.MediaType(asset.Key)));
            if (asset.Key == book.Cover) item.SetAttributeValue("properties", "cover-image"); manifest.Add(item);
        }
        var body = new XElement(xhtml + "body"); var styles = new List<string>(); var toc = new XElement(xhtml + "ol");
        for (var i = 0; i < book.Parts.Count; i++)
        {
            var part = book.Parts[i]; var section = new XElement(xhtml + "section", new XAttribute("id", "part" + i));
            var doc = BookMarkup.Parse(part.Html);
            foreach (var style in doc.QuerySelectorAll("style").ToArray()) { styles.Add(style.TextContent); style.Remove(); }
            foreach (var node in doc.Body!.ChildNodes) if (ToXml(node, xhtml) is { } converted) section.Add(converted);
            body.Add(section); toc.Add(new XElement(xhtml + "li", new XElement(xhtml + "a", new XAttribute("href", "book.xhtml#part" + i), part.Title)));
        }
        var embeddedProperties = new List<string>();
        if (body.Descendants().Any(e => e.Name.NamespaceName == "http://www.w3.org/2000/svg")) embeddedProperties.Add("svg");
        if (body.Descendants().Any(e => e.Name.NamespaceName == "http://www.w3.org/1998/Math/MathML")) embeddedProperties.Add("mathml");
        if (embeddedProperties.Count > 0) manifest.Elements(ns + "item").First(e => e.Attribute("id")?.Value == "text").SetAttributeValue("properties", string.Join(" ", embeddedProperties));
        Entry(zip, "OEBPS/content.opf", new XDocument(new XElement(ns + "package", new XAttribute("version", "3.0"), new XAttribute("unique-identifier", "bookid"), metadata, manifest, new XElement(ns + "spine", new XElement(ns + "itemref", new XAttribute("idref", "text"))))).ToString());
        var head = new XElement(xhtml + "head", new XElement(xhtml + "title", book.Title), new XElement(xhtml + "style", "body{font-size:" + options.FontSize.ToString(System.Globalization.CultureInfo.InvariantCulture) + "pt} img{max-width:100%;height:auto} table{border-collapse:collapse} td,th{border:1px solid #888}" + string.Join("\n", styles)));
        Entry(zip, "OEBPS/book.xhtml", new XDocument(new XElement(xhtml + "html", new XAttribute("lang", book.Language), head, body)).ToString());
        // The navigation document belongs to the same book and shares its page styles.
        // This also avoids Calibre 9.16 leaving a dangling duplicate page_styles1.css link.
        XNamespace epub = "http://www.idpf.org/2007/ops";
        Entry(zip, "OEBPS/nav.xhtml", new XDocument(new XElement(xhtml + "html", new XAttribute(XNamespace.Xmlns + "epub", epub), new XElement(xhtml + "head", new XElement(xhtml + "title", "Оглавление"), new XElement(head.Element(xhtml + "style")!)), new XElement(xhtml + "body", new XElement(xhtml + "nav", new XAttribute(epub + "type", "toc"), options.GenerateTableOfContents ? null : new XAttribute("hidden", "hidden"), toc)))).ToString());
    }
    internal static XNode? ToXml(INode node, XNamespace ns)
    {
        if (node is IText text) return new XText(text.Data);
        if (node is not IElement e) return null;
        var elementNamespace = e.NamespaceUri is "http://www.w3.org/2000/svg" or "http://www.w3.org/1998/Math/MathML" or "http://www.w3.org/1999/xhtml" ? (XNamespace)e.NamespaceUri : ns;
        var element = new XElement(elementNamespace + e.LocalName);
        // HTML parsers recognize the standard xlink prefix on foreign SVG attributes.
        // A generated pN prefix would lose that namespace on the next HTML DOM round trip.
        if (elementNamespace.NamespaceName == "http://www.w3.org/2000/svg" && e.LocalName == "svg") element.SetAttributeValue(XNamespace.Xmlns + "xlink", "http://www.w3.org/1999/xlink");
        foreach (var attribute in e.Attributes)
        {
            if (attribute.Name == "xmlns" || attribute.Prefix == "xmlns" || attribute.Name.StartsWith("xmlns:", StringComparison.Ordinal)) continue;
            var uri = attribute.NamespaceUri;
            if (string.IsNullOrEmpty(uri)) uri = attribute.Prefix switch
            {
                "xlink" => "http://www.w3.org/1999/xlink", "xml" => "http://www.w3.org/XML/1998/namespace", "epub" => "http://www.idpf.org/2007/ops", _ => null
            };
            if (!string.IsNullOrEmpty(uri)) element.SetAttributeValue((XNamespace)uri + attribute.LocalName, attribute.Value);
            else if (!attribute.Name.Contains(':')) element.SetAttributeValue(attribute.Name, attribute.Value);
            else if (attribute.Name.StartsWith("xlink:", StringComparison.Ordinal)) element.SetAttributeValue((XNamespace)"http://www.w3.org/1999/xlink" + attribute.Name[6..], attribute.Value);
            else if (attribute.Name.StartsWith("epub:", StringComparison.Ordinal)) element.SetAttributeValue((XNamespace)"http://www.idpf.org/2007/ops" + attribute.Name[5..], attribute.Value);
            else if (attribute.Name.StartsWith("xml:", StringComparison.Ordinal)) element.SetAttributeValue(XNamespace.Xml + attribute.Name[4..], attribute.Value);
        }
        if (e.LocalName == "img" && element.Attribute("alt") is null) element.SetAttributeValue("alt", "");
        foreach (var child in e.ChildNodes) if (ToXml(child, elementNamespace) is { } converted) element.Add(converted);
        return element;
    }
    private static void Fb2(BookContent book, string path)
    {
        XNamespace ns = "http://www.gribuser.ru/xml/fictionbook/2.0", link = "http://www.w3.org/1999/xlink";
        var ids = book.Assets.Where(x => BookMarkup.MediaType(x.Key).StartsWith("image/")).ToDictionary(x => x.Key, x => "img" + Array.IndexOf(book.Assets.Keys.ToArray(), x.Key));
        XNode? ConvertNode(INode node)
        {
            if (node is IText t) return new XText(t.Data);
            if (node is not IElement e) return null;
            if (e.LocalName is "style" or "script") return null;
            if (e.LocalName == "img") return ids.TryGetValue(e.GetAttribute("src") ?? "", out var imageId) ? new XElement(ns + "image", new XAttribute(link + "href", "#" + imageId)) : throw new InvalidDataException("BookConverter.Error.ImageUnavailable");
            var tag = e.LocalName switch { "h1" or "h2" or "h3" or "h4" or "h5" or "h6" => "subtitle", "strong" or "b" => "strong", "em" or "i" => "emphasis", "s" or "del" => "strikethrough", "sub" => "sub", "sup" => "sup", "a" => "a", "code" => "code", "td" => "td", "th" => "th", "tr" => "tr", "table" => "table", "br" => "empty-line", "section" => "section", "blockquote" => "cite", "p" or "li" or "pre" => "p", _ => "section" };
            var output = new XElement(ns + tag);
            if (!string.IsNullOrEmpty(e.Id) && tag is "p" or "subtitle" or "section" or "table" or "cite") output.SetAttributeValue("id", e.Id);
            if (tag == "a") output.SetAttributeValue(link + "href", e.GetAttribute("href") ?? "#");
            foreach (var child in e.ChildNodes) if (ConvertNode(child) is { } x) output.Add(x);
            return output;
        }
        var ti = new XElement(ns + "title-info", new XElement(ns + "genre", "unrecognised"));
        foreach (var author in book.Author.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)) ti.Add(new XElement(ns + "author", new XElement(ns + "nickname", author)));
        if (!ti.Elements(ns + "author").Any()) ti.Add(new XElement(ns + "author", new XElement(ns + "nickname", "Автор не указан")));
        ti.Add(new XElement(ns + "book-title", book.Title));
        if (book.Description.Length > 0) ti.Add(new XElement(ns + "annotation", new XElement(ns + "p", book.Description)));
        if (book.Cover is not null && ids.TryGetValue(book.Cover, out var coverId)) ti.Add(new XElement(ns + "coverpage", new XElement(ns + "image", new XAttribute(link + "href", "#" + coverId))));
        ti.Add(new XElement(ns + "lang", book.Language));
        var body = new XElement(ns + "body");
        foreach (var part in book.Parts)
        {
            var section = new XElement(ns + "section", book.UseExistingPartTitles ? null : new XElement(ns + "title", new XElement(ns + "p", part.Title)));
            foreach (var child in BookMarkup.Parse(part.Html).Body!.ChildNodes) if (ConvertNode(child) is { } x) section.Add(x);
            body.Add(section);
        }
        var root = new XElement(ns + "FictionBook", new XAttribute(XNamespace.Xmlns + "l", link), new XElement(ns + "description", ti, new XElement(ns + "document-info", new XElement(ns + "author", new XElement(ns + "nickname", "LOPATA")), new XElement(ns + "date", DateTime.UtcNow.ToString("yyyy-MM-dd")), new XElement(ns + "id", Guid.NewGuid()), new XElement(ns + "version", "1.0"))), body);
        foreach (var image in ids) root.Add(new XElement(ns + "binary", new XAttribute("id", image.Value), new XAttribute("content-type", BookMarkup.MediaType(image.Key)), System.Convert.ToBase64String(book.Assets[image.Key])));
        NormalizeFb2(root, ns);
        new XDocument(root).Save(path);
        book.Warnings.Add("BookConverter.Warning.Fb2Layout");
    }
    private static void NormalizeFb2(XElement root, XNamespace ns)
    {
        // FB2 section children must be either only child sections or only text blocks.
        // HTML containers are flattened inside paragraphs and tables, never emitted as invalid mixed blocks.
        foreach (var table in root.Descendants(ns + "table").ToArray())
        {
            var rows = table.Descendants(ns + "tr").Select(r => new XElement(r)).ToArray(); table.RemoveNodes(); table.Add(rows);
        }
        var inline = new HashSet<string> { "strong", "emphasis", "strikethrough", "sub", "sup", "code", "a", "image", "style" };
        foreach (var paragraph in root.Descendants().Where(e => e.Name.Namespace == ns && e.Name.LocalName is "p" or "subtitle" or "td" or "th").ToArray())
        {
            foreach (var bad in paragraph.Descendants().Where(e => !inline.Contains(e.Name.LocalName)).Reverse().ToArray())
                bad.ReplaceWith(bad.Name.LocalName == "empty-line" ? new XText("\n") : bad.Nodes().ToArray());
        }
        foreach (var section in root.Descendants(ns + "section").Reverse().ToArray())
        {
            foreach (var text in section.Nodes().OfType<XText>().Where(t => !string.IsNullOrWhiteSpace(t.Value)).ToArray()) text.ReplaceWith(new XElement(ns + "p", text.Value));
            if (section.Elements(ns + "section").Any())
            {
                var header = section.Elements(ns + "title").FirstOrDefault();
                var content = section.Nodes().Where(n => n != header && (n is not XText t || !string.IsNullOrWhiteSpace(t.Value))).ToArray();
                foreach (var node in content) node.Remove();
                XElement? group = null;
                foreach (var node in content)
                {
                    if (node is XElement e && e.Name == ns + "section") { section.Add(e); group = null; }
                    else { if (group is null) { group = new XElement(ns + "section"); section.Add(group); } group.Add(node); }
                }
            }
            else
            {
                var first = section.Elements().FirstOrDefault(e => e.Name.LocalName != "title");
                if (first?.Name == ns + "image") first.AddBeforeSelf(new XElement(ns + "empty-line"));
            }
        }
    }
    private static void Cbz(BookContent book, string path, BookConversionOptions options, CancellationToken ct)
    {
        var content = BookMarkup.Parse(string.Join("", book.Parts.Select(p => p.Html)));
        foreach (var style in content.QuerySelectorAll("style,script")) style.Remove();
        if (content.Body!.TextContent.Trim().Length > 0)
            throw new NotSupportedException("BookConverter.Error.CbzText");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create); var index = 0;
        foreach (var part in book.Parts)
            foreach (var img in BookMarkup.Parse(part.Html).QuerySelectorAll("img"))
            {
                ct.ThrowIfCancellationRequested(); var key = img.GetAttribute("src") ?? "";
                if (!book.Assets.TryGetValue(key, out var bytes)) throw new InvalidDataException("BookConverter.Error.ImageUnavailable");
                using var bitmap = SKBitmap.Decode(bytes) ?? throw new InvalidDataException("BookConverter.Error.ImageUnavailable");
                using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, options.ImageQuality);
                using var output = zip.CreateEntry((++index).ToString("D6") + ".jpg").Open(); data.SaveTo(output);
            }
        if (index == 0) throw new InvalidDataException("BookConverter.Error.NoPages");
        Entry(zip, "ComicInfo.xml", new XDocument(new XElement("ComicInfo", new XElement("Title", book.Title), new XElement("Writer", book.Author), new XElement("LanguageISO", book.Language), new XElement("Summary", book.Description), new XElement("PageCount", index))).ToString());
    }
    private static void Pdf(BookContent book, string path, BookConversionOptions options, CancellationToken ct, OutputProvenance? facts)
    {
        facts ??= new OutputProvenance { Scenario = "books/convert/pdf" };
        using var output = File.Create(path); using var pdf = SKDocument.CreatePdf(output, new SKDocumentPdfMetadata { Title = book.Title, Author = book.Author, Creator = "LOPATA " + facts.ProgramVersion, Subject = "LOPATA.Provenance=" + facts.Json(), Keywords = book.SourceHistory.Length > 0 ? "LOPATA.SourceProvenance=" + System.Text.Json.JsonSerializer.Serialize(book.SourceHistory) : "", Creation = facts.ExportedUtc.UtcDateTime, Modified = facts.ExportedUtc.UtcDateTime, EncodingQuality = options.ImageQuality }) ?? throw new InvalidOperationException("BookConverter.Error.PdfUnavailable");
        using var typeface = SKTypeface.FromFamilyName("Arial"); using var font = new SKFont(typeface, (float)options.FontSize); using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        var width = 595f; var height = 842f; var margin = (float)(options.MarginMillimeters * 72 / 25.4); var y = margin;
        var canvas = pdf.BeginPage(width, height);
        void Page() { pdf.EndPage(); canvas = pdf.BeginPage(width, height); y = margin; }
        void Text(string text)
        {
            foreach (var paragraph in text.Replace("\r", "").Replace("\t", "   ").Split('\n'))
            {
                var line = "";
                foreach (var word in paragraph.Split(' '))
                {
                    var candidate = line.Length == 0 ? word : line + " " + word;
                    if (font.MeasureText(candidate) > width - 2 * margin && line.Length > 0) { Draw(line); line = word; }
                    else line = candidate;
                }
                Draw(line);
            }
        }
        void Draw(string line) { ct.ThrowIfCancellationRequested(); if (y + font.Size * 1.5f > height - margin) Page(); y += font.Size * 1.3f; canvas.DrawText(line, margin, y, font, paint); }
        var buffer = new StringBuilder();
        void FlushText()
        {
            if (buffer.ToString().Trim().Length > 0) Text(buffer.ToString().Trim());
            buffer.Clear();
        }
        void Image(IElement image)
        {
            FlushText();
            if (!book.Assets.TryGetValue(image.GetAttribute("src") ?? "", out var bytes)) throw new InvalidDataException("BookConverter.Error.ImageUnavailable");
            using var bitmap = SKBitmap.Decode(bytes) ?? throw new InvalidDataException("BookConverter.Error.ImageUnavailable");
            var scale = Math.Min((width - 2 * margin) / bitmap.Width, (height - 2 * margin) / bitmap.Height); var w = bitmap.Width * scale; var h = bitmap.Height * scale;
            if (y + h > height - margin) Page();
            canvas.DrawBitmap(bitmap, new SKRect(margin, y, margin + w, y + h)); y += h + 10;
        }
        void Visit(INode node, bool preformatted = false)
        {
            ct.ThrowIfCancellationRequested();
            if (node is IText text)
            {
                buffer.Append(preformatted ? text.Data : System.Text.RegularExpressions.Regex.Replace(text.Data, @"\s+", " "));
                return;
            }
            if (node is not IElement element || element.LocalName is "style" or "script") return;
            if (element.LocalName == "img") { Image(element); return; }
            if (element.LocalName == "br") { buffer.Append('\n'); return; }
            var block = element.LocalName is "p" or "div" or "section" or "article" or "header" or "footer" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "blockquote" or "pre" or "ul" or "ol" or "li" or "table" or "tr";
            if (block) FlushText();
            foreach (var child in element.ChildNodes) Visit(child, preformatted || element.LocalName == "pre");
            if (element.LocalName is "td" or "th") buffer.Append('\t');
            if (block) FlushText();
        }
        foreach (var part in book.Parts)
        {
            var document = BookMarkup.Parse(part.Html);
            foreach (var node in document.Body!.ChildNodes) Visit(node);
            FlushText();
        }
        pdf.EndPage(); pdf.Close();
        book.Warnings.Add("BookConverter.Warning.PdfLayout");
    }
}
