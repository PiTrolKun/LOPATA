using System.IO;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace AIHub.Services.Books;

internal sealed class BookContent
{
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public string Language { get; set; } = "und";
    public string Description { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string Rights { get; set; } = "";
    public List<BookPart> Parts { get; } = [];
    public Dictionary<string, byte[]> Assets { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Warnings { get; } = [];
    public string? Cover { get; set; }
    public string[] SourceHistory { get; set; } = [];
    public bool UseExistingPartTitles { get; set; }
    internal Dictionary<int, BookPdfWord[]> ExactPageWords { get; } = [];
    public string PlainText => string.Join("\n\n", Parts.Select(p => BookMarkup.Parse(p.Html).Body?.TextContent ?? ""));
}
internal sealed record BookPdfWord(string Text, double Left, double Top, double Right, double Bottom);
internal sealed record BookPart(string Title, string Html);

internal static class BookMarkup
{
    public static AngleSharp.Html.Dom.IHtmlDocument Parse(string html) => new HtmlParser().ParseDocument(html);
    public static string Escape(string? value) => System.Net.WebUtility.HtmlEncode(value ?? "");
    public static string MediaType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp",
        ".svg" => "image/svg+xml", ".bmp" => "image/bmp", ".tif" or ".tiff" => "image/tiff", ".css" => "text/css", ".woff" => "font/woff", ".woff2" => "font/woff2", ".ttf" => "font/ttf", ".otf" => "font/otf", _ => "application/octet-stream"
    };
    public static XDocument Xml(Stream stream)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = 64 * 1024 * 1024 });
        return XDocument.Load(reader);
    }
    public static XDocument Xml(string text) { using var s = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)); return Xml(s); }
    public static Dictionary<string, byte[]> ReadZip(string path, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(path);
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long total = 0, actualTotal = 0;
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/');
            if (name.EndsWith('/')) continue;
            if (name.StartsWith('/') || name.Split('/').Contains("..") || name.Contains(':') || !result.TryAdd(name, []))
                throw new InvalidDataException("BookConverter.Error.ArchivePaths");
            // Bounds protect the in-memory structural conversion; they are not a silent truncation policy.
            total += entry.Length;
            if (entry.Length > 128L * 1024 * 1024 || total > 768L * 1024 * 1024 || result.Count > 50000)
                throw new InvalidDataException("BookConverter.Error.ArchiveBudget");
            using var input = entry.Open(); using var output = new MemoryStream();
            var buffer = new byte[64 * 1024]; int read;
            while ((read = input.Read(buffer)) > 0)
            {
                ct.ThrowIfCancellationRequested(); actualTotal += read;
                if (output.Length + read > 128L * 1024 * 1024 || actualTotal > 768L * 1024 * 1024) throw new InvalidDataException("BookConverter.Error.ArchiveBudgetExceeded");
                output.Write(buffer, 0, read);
            }
            result[name] = output.ToArray();
        }
        return result;
    }
    public static string Resolve(string relativeBase, string target)
    {
        target = Uri.UnescapeDataString(target.Split('#')[0].Split('?')[0]).Replace('\\', '/');
        if (Uri.TryCreate(target, UriKind.Absolute, out _) || target.StartsWith('/')) throw new InvalidDataException("BookConverter.Error.ExternalResource");
        var stack = relativeBase.Split('/').SkipLast(1).Where(s => s.Length > 0).ToList();
        foreach (var part in target.Split('/'))
            if (part == "..") { if (stack.Count == 0) throw new InvalidDataException("BookConverter.Error.ResourceOutside"); stack.RemoveAt(stack.Count - 1); }
            else if (part != "." && part.Length > 0) stack.Add(part);
        return string.Join('/', stack);
    }
    public static string Body(string title, string body, BookConversionOptions options) => "<!DOCTYPE html><html xmlns=\"http://www.w3.org/1999/xhtml\"><head><meta charset=\"utf-8\"/><title>" + Escape(title) + "</title><style>body{font-size:" + options.FontSize.ToString(System.Globalization.CultureInfo.InvariantCulture) + "pt;margin:" + options.MarginMillimeters.ToString(System.Globalization.CultureInfo.InvariantCulture) + "mm}img{max-width:100%;height:auto}table{border-collapse:collapse}td,th{border:1px solid #888;padding:.3em}</style></head><body>" + body + "</body></html>";

    public static void Sanitize(IDocument doc)
    {
        foreach (var el in doc.QuerySelectorAll("script,iframe,object,embed,form,input,button,base")) el.Remove();
        foreach (var el in doc.All)
            foreach (var attr in el.Attributes.ToArray())
                if (attr.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase) || ((attr.Name is "href" or "src") && attr.Value.TrimStart().StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))) el.RemoveAttribute(attr.Name);
    }

    public static BookContent Merge(IReadOnlyList<BookContent> books, string title)
    {
        var result = new BookContent { Title = title, Language = books.Select(b => b.Language).Distinct().Count() == 1 ? books[0].Language : "und" };
        result.Author = string.Join("; ", books.Select(b => b.Author).Where(x => x.Length > 0).Distinct());
        result.Description = string.Join("\n\n", books.Select(b => b.Description).Where(x => x.Length > 0).Distinct());
        result.Publisher = string.Join("; ", books.Select(b => b.Publisher).Where(x => x.Length > 0).Distinct());
        result.Rights = string.Join("; ", books.Select(b => b.Rights).Where(x => x.Length > 0).Distinct());
        for (var i = 0; i < books.Count; i++)
        {
            var prefix = "b" + i + "_"; var book = books[i];
            foreach (var pair in book.Assets) result.Assets[prefix + pair.Key] = pair.Value;
            foreach (var part in book.Parts)
            {
                var doc = Parse(part.Html);
                foreach (var el in doc.All)
                {
                    if (!string.IsNullOrEmpty(el.Id)) el.Id = prefix + el.Id;
                    foreach (var attr in new[] { "href", "src" })
                    {
                        var value = el.GetAttribute(attr);
                        if (value is null) continue;
                        if (value.StartsWith('#')) el.SetAttribute(attr, "#" + prefix + value[1..]);
                        else if (book.Assets.ContainsKey(value)) el.SetAttribute(attr, prefix + value);
                    }
                }
                foreach (var style in doc.QuerySelectorAll("style")) foreach (var asset in book.Assets.Keys) style.TextContent = style.TextContent.Replace(asset, prefix + asset, StringComparison.Ordinal);
                result.Parts.Add(new BookPart(part.Title, doc.Body!.InnerHtml));
            }
            result.Cover ??= book.Cover is null ? null : prefix + book.Cover;
            result.Warnings.AddRange(book.Warnings);
        }
        return result;
    }
}
