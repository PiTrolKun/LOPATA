using System.IO;
using System.Text;
using System.Xml.Linq;
using AngleSharp.Dom;
using Markdig;

namespace AIHub.Services.Books;

internal static class BookConversionReader
{
    public static BookContent Read(string path, BookConversionOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (ext == "epub") return Epub(path, ct);
        if (ext == "fb2") return Fb2(path);
        if (ext == "docx") return BookConversionDocx.Read(path, ct);
        if (ext == "cbz") return ImagesFromZip(path, ct);
        if (BookConversionFormats.NativeInputs.Contains(ext) && ext is not ("txt" or "md" or "markdown" or "html" or "htm" or "xhtml"))
            return Image(path, ct);
        var book = new BookContent { Title = Path.GetFileNameWithoutExtension(path) };
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var text = File.ReadAllText(path, Encoding.GetEncoding(options.TextEncoding));
        if (ext is "txt" or "md" or "markdown") text = System.Text.RegularExpressions.Regex.Replace(text, @"(?m)^(?:\[LOPATA\.(?:SourceProvenance|Provenance)=.*\]|<!-- LOPATA\.(?:SourceProvenance|Provenance)=.* -->)\r?\n(?:\r?\n)?", "");
        var html = ext is "md" or "markdown" ? Markdown.ToHtml(text, new MarkdownPipelineBuilder().UseAdvancedExtensions().Build())
            : ext == "txt" ? "<pre>" + BookMarkup.Escape(text) + "</pre>" : text;
        var doc = BookMarkup.Parse(html); BookMarkup.Sanitize(doc);
        if (!string.IsNullOrEmpty(doc.Title)) book.Title = doc.Title;
        book.Author = doc.QuerySelector("meta[name='author']")?.GetAttribute("content") ?? "";
        book.Language = doc.DocumentElement?.GetAttribute("lang") ?? "und";
        foreach (var image in doc.QuerySelectorAll("img"))
        {
            var src = image.GetAttribute("src"); if (string.IsNullOrEmpty(src)) continue;
            if (src.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = BookConversionResources.ReadDataResource(src, out var mediaType);
                var type = mediaType[6..]; var key = "image" + book.Assets.Count + "." + (type switch { "jpeg" => "jpg", "svg+xml" => "svg", _ => type });
                book.Assets[key] = bytes; image.SetAttribute("src", key); continue;
            }
            if (Uri.TryCreate(src, UriKind.Absolute, out _)) { book.Warnings.Add("BookConverter.Warning.ExternalImage"); image.Remove(); continue; }
            var root = Path.GetFullPath(Path.GetDirectoryName(path)! + Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(Path.Combine(root, Uri.UnescapeDataString(src.Split('#')[0])));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) throw new InvalidDataException("BookConverter.Error.MissingImage");
            var asset = "image" + book.Assets.Count + Path.GetExtension(full); book.Assets[asset] = BookConversionResources.LocalImage(full, root); image.SetAttribute("src", asset);
        }
        foreach (var style in doc.Head!.QuerySelectorAll("style").ToArray()) doc.Body!.InsertBefore(style, doc.Body.FirstChild);
        BookConversionResources.SanitizeStyles(doc, book);
        book.Parts.Add(new BookPart(book.Title, doc.Body!.InnerHtml));
        return book;
    }

    private static BookContent Epub(string path, CancellationToken ct)
    {
        var zip = BookMarkup.ReadZip(path, ct);
        if (zip.TryGetValue("META-INF/encryption.xml", out var encryption) && BookMarkup.Xml(Encoding.UTF8.GetString(encryption)).Descendants().Any(e => e.Name.LocalName == "EncryptedData"))
            throw new NotSupportedException("BookConverter.Error.EncryptedBook");
        if (!zip.TryGetValue("META-INF/container.xml", out var container)) throw new InvalidDataException("BookConverter.Error.InvalidEpub");
        var root = BookMarkup.Xml(Encoding.UTF8.GetString(container)).Descendants().First(e => e.Name.LocalName == "rootfile").Attribute("full-path")?.Value ?? throw new InvalidDataException("BookConverter.Error.InvalidEpub");
        if (!zip.TryGetValue(root, out var opfBytes)) throw new InvalidDataException("BookConverter.Error.InvalidEpub");
        var opf = BookMarkup.Xml(Encoding.UTF8.GetString(opfBytes));
        string Meta(string name) => string.Join("; ", opf.Descendants().Where(e => e.Name.LocalName == name).Select(e => e.Value));
        var book = new BookContent { Title = Meta("title"), Author = Meta("creator"), Language = Meta("language"), Description = Meta("description"), Publisher = Meta("publisher"), Rights = Meta("rights") };
        var items = opf.Descendants().Where(e => e.Name.LocalName == "item").ToDictionary(e => e.Attribute("id")!.Value);
        var assetMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in items.Values.Where(i => !(i.Attribute("media-type")?.Value.Contains("html") ?? false)))
        {
            var resolved = BookMarkup.Resolve(root, item.Attribute("href")!.Value);
            if (!zip.TryGetValue(resolved, out var bytes)) continue;
            if (Path.GetExtension(resolved).Equals(".svg", StringComparison.OrdinalIgnoreCase)) BookConversionResources.ValidateSvg(bytes);
            var key = "asset" + book.Assets.Count + Path.GetExtension(resolved); assetMap[resolved] = key; book.Assets[key] = bytes;
            if (item.Attribute("properties")?.Value.Split(' ').Contains("cover-image") == true || opf.Descendants().Any(e => e.Name.LocalName == "meta" && e.Attribute("name")?.Value == "cover" && e.Attribute("content")?.Value == item.Attribute("id")?.Value)) book.Cover = key;
        }
        string RebaseCss(string css, string cssSource) => System.Text.RegularExpressions.Regex.Replace(css, @"url\(\s*['"" ]?(?<url>[^)'""]+)['"" ]?\s*\)", match =>
        {
            var url = match.Groups["url"].Value.Trim();
            if (url.StartsWith('#') || Uri.TryCreate(url, UriKind.Absolute, out _)) return match.Value;
            var resolvedResource = BookMarkup.Resolve(cssSource, url);
            return assetMap.TryGetValue(resolvedResource, out var mapped) ? "url('" + mapped + "')" : match.Value;
        });
        foreach (var stylesheet in assetMap.Where(pair => Path.GetExtension(pair.Key).Equals(".css", StringComparison.OrdinalIgnoreCase)))
        {
            var stylesheetDocument = BookMarkup.Parse(""); var style = stylesheetDocument.CreateElement("style");
            style.TextContent = RebaseCss(Encoding.UTF8.GetString(book.Assets[stylesheet.Value]), stylesheet.Key);
            stylesheetDocument.Head!.AppendChild(style); BookConversionResources.SanitizeStyles(stylesheetDocument, book);
            book.Assets[stylesheet.Value] = Encoding.UTF8.GetBytes(style.TextContent);
        }
        var spine = opf.Descendants().Where(e => e.Name.LocalName == "itemref").Select(e => items.GetValueOrDefault(e.Attribute("idref")!.Value)).Where(e => e is not null).ToArray();
        var chapterMap = spine.Select((e, i) => (Path: BookMarkup.Resolve(root, e!.Attribute("href")!.Value), Prefix: "c" + i + "_")).ToDictionary(x => x.Path, x => x.Prefix);
        foreach (var entry in spine)
        {
            ct.ThrowIfCancellationRequested();
            var source = BookMarkup.Resolve(root, entry!.Attribute("href")!.Value);
            if (!zip.TryGetValue(source, out var htmlBytes)) throw new InvalidDataException("BookConverter.Error.InvalidEpub");
            var doc = BookMarkup.Parse(Encoding.UTF8.GetString(htmlBytes)); BookMarkup.Sanitize(doc); var prefix = chapterMap[source];
            var rebasedStyles = new HashSet<IElement>();
            foreach (var el in doc.All)
            {
                if (!string.IsNullOrEmpty(el.Id)) el.Id = prefix + el.Id;
                foreach (var attr in el.Attributes.Where(a => a.LocalName is "src" or "href").ToArray())
                {
                    var value = attr.Value; if (string.IsNullOrEmpty(value)) continue;
                    if (Uri.TryCreate(value, UriKind.Absolute, out _)) continue;
                    var hash = value.IndexOf('#'); var anchor = hash >= 0 ? value[(hash + 1)..] : "";
                    var resolved = value.StartsWith('#') ? source : BookMarkup.Resolve(source, value);
                    if (assetMap.TryGetValue(resolved, out var asset))
                    {
                        if (attr.LocalName == "href" && Path.GetExtension(asset).Equals(".css", StringComparison.OrdinalIgnoreCase))
                        {
                            // Stylesheet assets were rebased and sanitized above. Do not map them twice.
                            var style = doc.CreateElement("style"); style.TextContent = Encoding.UTF8.GetString(book.Assets[asset]); rebasedStyles.Add(style); el.Parent?.AppendChild(style); el.Remove();
                        }
                        else attr.Value = asset;
                    }
                    else if (chapterMap.TryGetValue(resolved, out var targetPrefix)) attr.Value = "#" + targetPrefix + (anchor.Length > 0 ? anchor : "start");
                }
            }
            foreach (var style in doc.QuerySelectorAll("style").Where(style => !rebasedStyles.Contains(style))) style.TextContent = RebaseCss(style.TextContent, source);
            foreach (var element in doc.All) if (element.GetAttribute("style") is { } inlineStyle) element.SetAttribute("style", RebaseCss(inlineStyle, source));
            BookConversionResources.SanitizeStyles(doc, book);
            foreach (var svg in doc.QuerySelectorAll("svg").Where(e => e.ParentElement?.NamespaceUri != "http://www.w3.org/2000/svg"))
            {
                var markup = BookConversionWriter.ToXml(svg, "http://www.w3.org/2000/svg")?.ToString() ?? throw new InvalidDataException("BookConverter.Error.InvalidImage");
                BookConversionResources.ValidateSvg(Encoding.UTF8.GetBytes(markup), book.Assets.Keys.ToHashSet(StringComparer.Ordinal));
            }
            book.Parts.Add(new BookPart(string.IsNullOrEmpty(doc.Title) ? "Раздел " + (book.Parts.Count + 1) : doc.Title, "<span id=\"" + prefix + "start\"></span>" + string.Join("", doc.Head!.QuerySelectorAll("style").Select(e => e.OuterHtml)) + doc.Body!.InnerHtml));
        }
        if (book.Parts.Count == 0) throw new InvalidDataException("BookConverter.Error.InvalidEpub");
        return book;
    }

    private static BookContent Fb2(string path)
    {
        using var input = File.OpenRead(path); var xml = BookMarkup.Xml(input);
        var ti = xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "title-info") ?? throw new InvalidDataException("BookConverter.Error.InvalidFb2");
        string Meta(string name) => ti.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value ?? "";
        var book = new BookContent { Title = Meta("book-title"), Language = Meta("lang"), Description = Meta("annotation"), Author = string.Join("; ", ti.Elements().Where(e => e.Name.LocalName == "author").Select(e => string.Join(" ", e.Elements().Select(x => x.Value)))) };
        var ids = new Dictionary<string, string>();
        foreach (var binary in xml.Descendants().Where(e => e.Name.LocalName == "binary"))
        {
            var id = binary.Attribute("id")?.Value ?? throw new InvalidDataException("BookConverter.Error.InvalidFb2");
            var extension = binary.Attribute("content-type")?.Value switch { "image/png" => ".png", "image/jpeg" => ".jpg", "image/gif" => ".gif", "image/svg+xml" => ".svg", _ => ".bin" };
            var key = "image" + book.Assets.Count + extension; ids[id] = key; book.Assets[key] = Convert.FromBase64String(binary.Value);
            if (extension == ".svg") BookConversionResources.ValidateSvg(book.Assets[key]);
        }
        string Render(XNode node)
        {
            if (node is XText text) return BookMarkup.Escape(text.Value);
            if (node is not XElement e) return "";
            var name = e.Name.LocalName; var inner = string.Concat(e.Nodes().Select(Render));
            var id = e.Attribute("id") is { } a ? " id=\"" + BookMarkup.Escape(a.Value) + "\"" : "";
            if (name == "image") { var link = e.Attributes().FirstOrDefault(a => a.Name.LocalName == "href")?.Value.TrimStart('#') ?? ""; return "<img src=\"" + BookMarkup.Escape(ids.GetValueOrDefault(link) ?? throw new InvalidDataException("BookConverter.Error.InvalidFb2")) + "\"/>"; }
            if (name == "a") return "<a href=\"" + BookMarkup.Escape(e.Attributes().FirstOrDefault(a => a.Name.LocalName == "href")?.Value) + "\"" + id + ">" + inner + "</a>";
            var tag = name switch { "section" => "section", "title" => "h2", "subtitle" => "h3", "p" => "p", "strong" => "strong", "emphasis" => "em", "strikethrough" => "s", "sup" => "sup", "sub" => "sub", "code" => "code", "empty-line" => "br", "v" => "p", "stanza" => "blockquote", "poem" => "section", "cite" => "blockquote", "epigraph" => "blockquote", "table" => "table", "tr" => "tr", "td" => "td", "th" => "th", _ => "div" };
            return "<" + tag + id + ">" + inner + "</" + tag + ">";
        }
        foreach (var body in xml.Root!.Elements().Where(e => e.Name.LocalName == "body"))
            book.Parts.Add(new BookPart(body.Attribute("name")?.Value ?? book.Title, string.Concat(body.Nodes().Select(Render))));
        var cover = ti.Descendants().FirstOrDefault(e => e.Name.LocalName == "coverpage")?.Descendants().FirstOrDefault(e => e.Name.LocalName == "image")?.Attributes().FirstOrDefault(a => a.Name.LocalName == "href")?.Value.TrimStart('#');
        book.Cover = cover is not null ? ids.GetValueOrDefault(cover) : null;
        return book;
    }
    private static BookContent Image(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var book = new BookContent { Title = Path.GetFileNameWithoutExtension(path) };
        if (Path.GetExtension(path).ToLowerInvariant() is ".tif" or ".tiff" or ".gif")
        {
            using var stream = File.OpenRead(path);
            var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(stream, System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            foreach (var frame in decoder.Frames)
            {
                ct.ThrowIfCancellationRequested();
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(frame));
                using var buffer = new MemoryStream(); encoder.Save(buffer);
                AddPage(book, "page" + book.Assets.Count.ToString("D6") + ".png", buffer.ToArray());
            }
            if (Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase)) book.Warnings.Add("BookConverter.Warning.GifFrames");
        }
        else AddPage(book, "page" + Path.GetExtension(path).ToLowerInvariant(), File.ReadAllBytes(path));
        return book;
    }
    private static void AddPage(BookContent book, string key, byte[] data)
    {
        book.Assets[key] = data; book.Cover ??= key;
        book.Parts.Add(new BookPart("Страница " + book.Parts.Count, "<p><img src=\"" + key + "\" alt=\"" + BookMarkup.Escape(book.Title) + "\"/></p>"));
    }
    private static BookContent ImagesFromZip(string path, CancellationToken ct)
    {
        var zip = BookMarkup.ReadZip(path, ct); var book = new BookContent { Title = Path.GetFileNameWithoutExtension(path) };
        foreach (var pair in zip.Where(p => new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp" }.Contains(Path.GetExtension(p.Key).ToLowerInvariant())).OrderBy(p => NaturalKey(p.Key), StringComparer.Ordinal))
        {
            var key = "page" + book.Assets.Count.ToString("D6") + Path.GetExtension(pair.Key); book.Assets[key] = pair.Value; book.Cover ??= key;
            book.Parts.Add(new BookPart(Path.GetFileNameWithoutExtension(pair.Key), "<p><img src=\"" + key + "\"/></p>"));
        }
        if (book.Parts.Count == 0) throw new InvalidDataException("BookConverter.Error.ImageUnavailable");
        return book;
    }
    private static string NaturalKey(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\d+", m => m.Value.PadLeft(20, '0'));
}
