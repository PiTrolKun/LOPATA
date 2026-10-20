using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using AngleSharp.Html.Parser;
using DocumentFormat.OpenXml.Packaging;

namespace AIHub.Services.Books;

/// <summary>Writes provenance into a newly staged book, never into an input path.</summary>
public static class BookOutputMetadata
{
    private const string Property = "LOPATA.Provenance";
    private const string Label = "LOPATA.Provenance=";
    private static readonly XNamespace Fb = "http://www.gribuser.ru/xml/fictionbook/2.0";
    private static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace Lopata = "https://lopata.app/ns/provenance/1.0/";
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    public static async Task WriteAndVerifyAsync(string path, string format, OutputProvenance facts, CancellationToken token, Encoding? outputEncoding = null)
    {
        token.ThrowIfCancellationRequested();
        var json = facts.Json();
        format = BookConversionFormats.Normalize(format);
        switch (format)
        {
            case "docx":
                using (var doc = WordprocessingDocument.Open(path, true)) OutputOfficeMetadata.Write(doc, facts);
                using (var zip = ZipFile.OpenRead(path))
                {
                    var custom = ReadXml(zip.GetEntry("docProps/custom.xml") ?? throw Missing());
                    Require(custom.Descendants().Any(e => e.Name.LocalName == "property" && (string?)e.Attribute("name") == Property && e.Value == json));
                }
                break;
            case "epub": case "kepub":
                WriteEpub(path, facts, json);
                using (var zip = ZipFile.OpenRead(path))
                {
                    var opf = ReadXml(zip.GetEntry(PackagePath(zip)) ?? throw Missing());
                    Require(opf.Descendants(Opf + "meta").Any(e => ((string?)e.Attribute("property") == "lopata:provenance" && e.Value == json)
                        || ((string?)e.Attribute("name") == Property && (string?)e.Attribute("content") == json)));
                }
                break;
            case "fb2":
            {
                var doc = ReadXml(path);
                var description = doc.Root?.Element(Fb + "description") ?? throw Missing();
                var info = description.Element(Fb + "document-info") ?? throw Missing();
                var program = info.Element(Fb + "program-used");
                if (program is null)
                {
                    program = new(Fb + "program-used");
                    var date = info.Element(Fb + "date");
                    if (date is null) throw Missing();
                    date.AddBeforeSelf(program);
                }
                var producer = "LOPATA " + facts.ProgramVersion;
                if (!program.Value.Contains(producer, StringComparison.Ordinal)) program.Value = string.IsNullOrWhiteSpace(program.Value) ? producer : program.Value + "; " + producer;
                foreach (var previous in description.Elements(Fb + "custom-info").Where(e => (string?)e.Attribute("info-type") == Property).ToArray()) previous.Remove();
                var custom = new XElement(Fb + "custom-info", new XAttribute("info-type", Property), json);
                var output = description.Elements(Fb + "output").FirstOrDefault();
                if (output is null) description.Add(custom); else output.AddBeforeSelf(custom);
                SaveXml(path, doc);
                Require(ReadXml(path).Descendants(Fb + "custom-info").Any(e => (string?)e.Attribute("info-type") == Property && e.Value == json));
                break;
            }
            case "odt":
            {
                XNamespace office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0", meta = "urn:oasis:names:tc:opendocument:xmlns:meta:1.0";
                using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
                {
                    var entry = zip.GetEntry("meta.xml") ?? throw Missing();
                    var doc = ReadXml(entry);
                    var values = doc.Root?.Element(office + "meta") ?? throw Missing();
                    foreach (var old in values.Elements(meta + "user-defined").Where(e => (string?)e.Attribute(meta + "name") == Property).ToArray()) old.Remove();
                    values.Add(new XElement(meta + "user-defined", new XAttribute(meta + "name", Property), new XAttribute(meta + "value-type", "string"), json));
                    values.SetElementValue(meta + "generator", "LOPATA " + facts.ProgramVersion);
                    ReplaceXml(zip, "meta.xml", doc);
                }
                using var result = ZipFile.OpenRead(path);
                Require(ReadXml(result.GetEntry("meta.xml") ?? throw Missing()).Descendants(meta + "user-defined").Any(e => (string?)e.Attribute(meta + "name") == Property && e.Value == json));
                break;
            }
            case "cbz":
                using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
                {
                    var doc = zip.GetEntry("ComicInfo.xml") is { } entry ? ReadXml(entry) : new XDocument(new XElement("ComicInfo"));
                    if (doc.Root?.Name.LocalName != "ComicInfo") throw Missing();
                    var notes = doc.Root.Element("Notes");
                    if (notes is null) doc.Root.Add(new XElement("Notes", Label + json));
                    else if (!notes.Value.Contains(Label + json, StringComparison.Ordinal)) notes.Value = notes.Value + "\n" + Label + json;
                    ReplaceXml(zip, "ComicInfo.xml", doc);
                    zip.GetEntry("LOPATA.Provenance.json")?.Delete();
                    await using var entryStream = zip.CreateEntry("LOPATA.Provenance.json").Open();
                    await entryStream.WriteAsync(Utf8.GetBytes(json), token);
                }
                using (var zip = ZipFile.OpenRead(path))
                {
                    using var reader = new StreamReader((zip.GetEntry("LOPATA.Provenance.json") ?? throw Missing()).Open(), Utf8);
                    Require(await reader.ReadToEndAsync(token) == json);
                }
                break;
            case "html":
            {
                var parser = new HtmlParser();
                var document = parser.ParseDocument(await File.ReadAllTextAsync(path, Utf8, token));
                var head = document.Head ?? throw Missing();
                foreach (var old in head.QuerySelectorAll("meta[name='LOPATA.Provenance']")) old.Remove();
                var metadata = document.CreateElement("meta"); metadata.SetAttribute("name", Property); metadata.SetAttribute("content", json); head.AppendChild(metadata);
                await File.WriteAllTextAsync(path, "<!DOCTYPE html>\n" + document.DocumentElement.OuterHtml, Utf8, token);
                var verified = parser.ParseDocument(await File.ReadAllTextAsync(path, Utf8, token));
                Require(verified.Head?.QuerySelector("meta[name='LOPATA.Provenance']")?.GetAttribute("content") == json);
                break;
            }
            case "txt": case "md":
            {
                var encoding = outputEncoding ?? Utf8;
                var body = await File.ReadAllTextAsync(path, encoding, token);
                var marker = format == "md" ? "<!-- " + Label + json + " -->\n\n" : "[" + Label + json + "]\n\n";
                await File.WriteAllTextAsync(path, marker + body, encoding, token);
                using var reader = new StreamReader(path, encoding);
                Require((await reader.ReadLineAsync(token))?.Contains(Label + json, StringComparison.Ordinal) == true);
                break;
            }
            case "rtf":
            {
                var bytes = await File.ReadAllBytesAsync(path, token);
                var text = Encoding.Latin1.GetString(bytes);
                if (!text.StartsWith("{\\rtf", StringComparison.Ordinal)) throw Missing();
                var end = 5; while (end < text.Length && char.IsAsciiDigit(text[end])) end++;
                var metadata = "{\\*\\userprops{\\propname " + Property + "}\\proptype30{\\staticval " + Rtf(json) + "}}";
                await File.WriteAllBytesAsync(path, Encoding.Latin1.GetBytes(text.Insert(end, metadata)), token);
                Require(Encoding.Latin1.GetString(await File.ReadAllBytesAsync(path, token)).Contains(metadata, StringComparison.Ordinal));
                break;
            }
            case "pdf":
                if (PdfContains(path, json)) break;
                var before = await FileMetadataExifTool.ReadAsync(path, token);
                var instructions = FileMetadataPolicy.RawValue(before, "XMP-photoshop:Instructions");
                var producerBefore = FileMetadataPolicy.RawValue(before, "PDF:Producer");
                await FileMetadataExifTool.WriteAsync(path, new Dictionary<string, string>
                {
                    ["XMP-xmp:CreatorTool"] = "LOPATA " + facts.ProgramVersion,
                    ["XMP-xmp:ModifyDate"] = facts.ExportedUtc.UtcDateTime.ToString("yyyy:MM:dd HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                    ["PDF:Producer"] = producerBefore + (producerBefore.Length == 0 ? "" : "; ") + "LOPATA " + facts.ProgramVersion,
                    ["XMP-photoshop:Instructions"] = instructions + (instructions.Length == 0 ? "" : "\n") + Label + json
                }, token);
                var after = await FileMetadataExifTool.ReadAsync(path, token);
                Require(FileMetadataPolicy.RawValue(after, "XMP-photoshop:Instructions").Contains(Label + json, StringComparison.Ordinal));
                break;
            case "azw3": case "mobi":
                await BookConverterRuntime.EnsureCalibreLicenseAsync(token);
                var converter = BookConverterRuntime.FindCalibre() ?? throw Missing();
                var tool = Path.Combine(Path.GetDirectoryName(converter)!, "ebook-meta.exe");
                var opfPath = path + "." + Guid.NewGuid().ToString("N") + ".opf";
                try
                {
                    await FileMetadataProcess.RunAsync(tool, [path, "--to-opf", opfPath], token);
                    var beforeOpf = ReadXml(opfPath);
                    var previous = beforeOpf.Descendants().Where(e => e.Name.LocalName == "contributor").Select(e => e.Value).FirstOrDefault() ?? "";
                    var description = beforeOpf.Descendants().FirstOrDefault(e => e.Name.LocalName == "description")?.Value ?? "";
                    var producer = previous + (previous.Length == 0 ? "" : "; ") + Label + json;
                    await FileMetadataProcess.RunAsync(tool, [path, "--book-producer", producer], token);
                    File.Delete(opfPath);
                    await FileMetadataProcess.RunAsync(tool, [path, "--to-opf", opfPath], token);
                    if (!ReadXml(opfPath).Descendants().Any(e => e.Name.LocalName == "contributor" && e.Value.Contains(Label + json, StringComparison.Ordinal)))
                    {
                        // Some Kindle metadata plugins ignore book-producer. Keep the original annotation
                        // and append the clearly delimited conversion record in the supported metadata field.
                        await FileMetadataProcess.RunAsync(tool, [path, "--comments", description + "\n\n[" + Label + json + "]"], token);
                        File.Delete(opfPath);
                        await FileMetadataProcess.RunAsync(tool, [path, "--to-opf", opfPath], token);
                        Require(ReadXml(opfPath).Descendants().Any(e => e.Name.LocalName == "description" && e.Value.Contains(Label + json, StringComparison.Ordinal)));
                    }
                }
                finally { if (File.Exists(opfPath)) File.Delete(opfPath); }
                break;
            default: throw new InvalidDataException("BookConverter.Error.MetadataFormat");
        }
    }

    private static bool PdfContains(string path, string json)
    {
        using var doc = UglyToad.PdfPig.PdfDocument.Open(path);
        return doc.Information.Subject?.Contains(Label + json, StringComparison.Ordinal) == true;
    }

    private static string Rtf(string value)
    {
        var result = new StringBuilder();
        foreach (var c in value)
            if (c is '\\' or '{' or '}') result.Append('\\').Append(c);
            else if (c > 127) result.Append("\\u").Append(unchecked((short)c).ToString(CultureInfo.InvariantCulture)).Append('?');
            else result.Append(c);
        return result.ToString();
    }

    private static void WriteEpub(string path, OutputProvenance facts, string json)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        var name = PackagePath(zip);
        var document = ReadXml(zip.GetEntry(name) ?? throw Missing());
        var root = document.Root ?? throw Missing();
        var metadata = root.Element(Opf + "metadata") ?? throw Missing();
        if (((string?)root.Attribute("version"))?.StartsWith("2", StringComparison.Ordinal) == true)
        {
            foreach (var old in metadata.Elements(Opf + "meta").Where(e => (string?)e.Attribute("name") == Property).ToArray()) old.Remove();
            metadata.Add(new XElement(Opf + "meta", new XAttribute("name", Property), new XAttribute("content", json)));
            // Preserve the EPUB 2 package; do not convert the vocabulary silently.
            ReplaceXml(zip, name, document);
            return;
        }
        var prefixes = ((string?)root.Attribute("prefix") ?? "").Trim();
        if (!prefixes.Contains("lopata:", StringComparison.Ordinal)) root.SetAttributeValue("prefix", prefixes + (prefixes.Length == 0 ? "" : " ") + "lopata: " + Lopata.NamespaceName);
        foreach (var old in metadata.Elements(Opf + "meta").Where(e => (string?)e.Attribute("property") == "lopata:provenance" || ((string?)e.Attribute("property") == "dcterms:modified" && e.Attribute("refines") is null)).ToArray()) old.Remove();
        metadata.Add(new XElement(Opf + "meta", new XAttribute("property", "lopata:provenance"), json),
            new XElement(Opf + "meta", new XAttribute("property", "dcterms:modified"), facts.ExportedUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)));
        // calibre's Kobo adapter adds reader scripts but does not always mark their
        // XHTML manifest items. EPUB 3 requires this declaration for scripted content.
        foreach (var item in root.Element(Opf + "manifest")?.Elements(Opf + "item") ?? [])
        {
            if ((string?)item.Attribute("media-type") != "application/xhtml+xml") continue;
            var href = (string?)item.Attribute("href");
            if (string.IsNullOrWhiteSpace(href)) continue;
            var resource = new Uri(new Uri("https://book.invalid/" + name), href);
            if (resource.Host != "book.invalid") continue;
            var entry = zip.GetEntry(Uri.UnescapeDataString(resource.AbsolutePath.TrimStart('/')));
            if (entry is null || entry.Length > 16 * 1024 * 1024) continue;
            using var reader = new StreamReader(entry.Open(), Utf8);
            if (new HtmlParser().ParseDocument(reader.ReadToEnd()).QuerySelector("script") is null) continue;
            var properties = ((string?)item.Attribute("properties") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (!properties.Contains("scripted")) properties.Add("scripted");
            item.SetAttributeValue("properties", string.Join(" ", properties));
        }
        ReplaceXml(zip, name, document);
    }

    private static string PackagePath(ZipArchive zip)
    {
        var container = ReadXml(zip.GetEntry("META-INF/container.xml") ?? throw Missing());
        var path = container.Descendants().FirstOrDefault(e => e.Name.LocalName == "rootfile")?.Attribute("full-path")?.Value ?? throw Missing();
        if (path.StartsWith('/') || path.Contains('\\') || path.Split('/').Any(p => p is ".." or "." || p.Contains(':'))) throw Missing();
        return path;
    }

    private static XDocument ReadXml(ZipArchiveEntry entry)
    {
        if (entry.Length > 16 * 1024 * 1024) throw new InvalidDataException("BookConverter.Error.MetadataTooLarge");
        using var input = entry.Open(); using var reader = XmlReader.Create(input, XmlSettings());
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }
    private static XDocument ReadXml(string path)
    {
        using var input = File.OpenRead(path); using var reader = XmlReader.Create(input, XmlSettings());
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }
    private static XmlReaderSettings XmlSettings() => new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 256L * 1024 * 1024 };
    private static void ReplaceXml(ZipArchive zip, string name, XDocument document)
    {
        zip.GetEntry(name)?.Delete();
        using var output = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        using var writer = XmlWriter.Create(output, new XmlWriterSettings { Encoding = Utf8, Indent = false, CloseOutput = false });
        document.Save(writer);
    }
    private static void SaveXml(string path, XDocument document)
    {
        using var writer = XmlWriter.Create(path, new XmlWriterSettings { Encoding = Utf8, Indent = false }); document.Save(writer);
    }
    private static InvalidDataException Missing() => new("BookConverter.Error.Metadata");
    private static void Require(bool value) { if (!value) throw Missing(); }
}
