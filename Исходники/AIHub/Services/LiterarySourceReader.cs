using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;
using AngleSharp.Html.Parser;

namespace AIHub.Services;

public sealed record LiterarySourceSection(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("section")] string Section,
    [property: JsonPropertyName("text")] string Text);

public static class LiterarySourceReader
{
    public const int MaxCharacters = 20_000_000;
    public static IReadOnlyList<LiterarySourceSection> Read(string path, string source, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (new FileInfo(path).Length > 100L * 1024 * 1024) throw new InvalidDataException("Source is larger than 100 MiB.");
        var result = new List<LiterarySourceSection>();
        void Add(string section, string text)
        {
            ct.ThrowIfCancellationRequested();
            text = text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            if (string.IsNullOrWhiteSpace(text)) return;
            if ((long)result.Sum(s => s.Text.Length) + text.Length > MaxCharacters) throw new InvalidDataException("Source text exceeds 20 million characters.");
            result.Add(new(source, section, text));
        }
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".txt":
                Add("text", File.ReadAllText(path, new UTF8Encoding(false, true))); break;
            case ".pdf":
                using (var pdf = UglyToad.PdfPig.PdfDocument.Open(path))
                    foreach (var page in pdf.GetPages()) { ct.ThrowIfCancellationRequested(); Add("page:" + page.Number,
                        UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor.ContentOrderTextExtractor.GetText(page)); }
                break;
            case ".epub":
                using (var archive = ZipFile.OpenRead(path))
                {
                    if (archive.Entries.Sum(e => e.Length) > 150L * 1024 * 1024) throw new InvalidDataException("EPUB expands beyond 150 MiB.");
                    XDocument Xml(string name)
                    {
                        var entry = archive.GetEntry(name) ?? throw new InvalidDataException("EPUB entry missing: " + name);
                        using var stream = entry.Open();
                        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxCharacters });
                        return XDocument.Load(reader);
                    }
                    var container = Xml("META-INF/container.xml");
                    var opf = container.Descendants().FirstOrDefault(e => e.Name.LocalName == "rootfile")?.Attribute("full-path")?.Value
                        ?? throw new InvalidDataException("EPUB package is missing.");
                    var package = Xml(opf);
                    var items = package.Descendants().Where(e => e.Name.LocalName == "manifest").Elements()
                        .Where(e => e.Name.LocalName == "item").ToDictionary(e => (string)e.Attribute("id")!, e => e);
                    var parser = new HtmlParser();
                    foreach (var item in package.Descendants().Where(e => e.Name.LocalName == "spine").Elements())
                    {
                        ct.ThrowIfCancellationRequested();
                        if (item.Name.LocalName != "itemref" || (string?)item.Attribute("linear") == "no") continue;
                        if (!items.TryGetValue((string?)item.Attribute("idref") ?? "", out var file)) throw new InvalidDataException("EPUB spine points to missing item.");
                        if (((string?)file.Attribute("properties") ?? "").Split(' ').Contains("nav")) continue;
                        var href = (string?)file.Attribute("href") ?? throw new InvalidDataException("EPUB item lacks href.");
                        var address = new Uri(new Uri("https://epub.invalid/" + opf), href);
                        if (address.Host != "epub.invalid") throw new InvalidDataException("External EPUB resource is unsupported.");
                        var name = Uri.UnescapeDataString(address.AbsolutePath.TrimStart('/'));
                        var entry = archive.GetEntry(name) ?? throw new InvalidDataException("EPUB chapter missing: " + name);
                        using var reader = new StreamReader(entry.Open(), Encoding.UTF8, true);
                        var doc = parser.ParseDocument(reader.ReadToEnd());
                        foreach (var node in doc.QuerySelectorAll("script,style")) node.Remove();
                        // Insert boundaries before flattening markup: adjacent paragraphs must not join words.
                        foreach (var node in doc.QuerySelectorAll("p,div,h1,h2,h3,h4,li,br")) node.Prepend(doc.CreateTextNode("\n"));
                        Add(name, doc.Body?.TextContent ?? doc.DocumentElement.TextContent);
                    }
                }
                break;
            default: throw new InvalidDataException("Supported sources: TXT (UTF-8), EPUB, PDF with a text layer.");
        }
        if (result.Count == 0) throw new InvalidDataException("No text found. Scanned books require OCR, which is not included in this step.");
        return result;
    }
}
