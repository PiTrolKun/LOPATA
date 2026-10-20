using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using DocumentFormat.OpenXml.CustomProperties;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.VariantTypes;

namespace AIHub.Services.Books;

/// <summary>Old generation history is inherited, separately from this model-free conversion.</summary>
internal static class BookConversionHistory
{
    public static string[] Read(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant(); var values = new List<string>();
        bool IsHistory(string name) => name is "LOPATA.Provenance" or "LOPATA.SourceProvenance" or "lopata:provenance" or "lopata:source-provenance";
        if (ext is "epub" or "kepub" or "docx" or "odt" or "cbz")
        {
            var zip = BookMarkup.ReadZip(path, CancellationToken.None);
            foreach (var item in zip.Where(p => p.Key.EndsWith(".opf", StringComparison.OrdinalIgnoreCase) || p.Key is "docProps/custom.xml" or "meta.xml" or "LOPATA.Provenance.json" or "LOPATA.SourceProvenance.json"))
            {
                var text = Encoding.UTF8.GetString(item.Value);
                if (item.Key.EndsWith(".json")) { values.Add(text); continue; }
                var xml = BookMarkup.Xml(text);
                foreach (var e in xml.Descendants())
                {
                    var name = e.Attributes().FirstOrDefault(a => a.Name.LocalName is "name" or "property")?.Value;
                    if (name is not null && IsHistory(name)) values.Add(e.Attribute("content")?.Value ?? e.Value);
                }
            }
        }
        else if (ext == "fb2")
        {
            using var stream = File.OpenRead(path); foreach (var e in BookMarkup.Xml(stream).Descendants().Where(e => e.Name.LocalName == "custom-info" && IsHistory(e.Attribute("info-type")?.Value ?? ""))) values.Add(e.Value);
        }
        else if (ext is "html" or "htm" or "xhtml")
        {
            var doc = BookMarkup.Parse(File.ReadAllText(path)); foreach (var e in doc.QuerySelectorAll("meta")) if (IsHistory(e.GetAttribute("name") ?? "")) values.Add(e.GetAttribute("content") ?? "");
        }
        else if (ext is "txt" or "md" or "markdown")
        {
            using var reader = new StreamReader(path); for (var i = 0; i < 8; i++)
            {
                var line = reader.ReadLine(); if (line is null) break;
                foreach (var label in new[] { "LOPATA.Provenance=", "LOPATA.SourceProvenance=" })
                {
                    var start = line.IndexOf(label, StringComparison.Ordinal); if (start < 0) continue;
                    var payload = line[(start + label.Length)..].Trim();
                    if (line.StartsWith("<!--", StringComparison.Ordinal)) payload = payload[..^3].TrimEnd(); else if (payload.EndsWith(']')) payload = payload[..^1];
                    values.Add(payload);
                }
            }
        }
        else if (ext == "pdf")
        {
            using var pdf = UglyToad.PdfPig.PdfDocument.Open(path); var subject = pdf.Information.Subject;
            if (subject?.StartsWith("LOPATA.Provenance=", StringComparison.Ordinal) == true) values.Add(subject[18..]);
        }
        return values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct().ToArray();
    }
    public static void Write(string path, string format, string[] history)
    {
        if (history.Length == 0) return;
        var json = JsonSerializer.Serialize(history.Distinct());
        if (format is "epub" or "kepub" or "docx" or "odt" or "cbz")
        {
            if (format == "docx")
            {
                using var doc = WordprocessingDocument.Open(path, true); var part = doc.CustomFilePropertiesPart ?? doc.AddCustomFilePropertiesPart(); part.Properties ??= new Properties();
                foreach (var old in part.Properties.Elements<CustomDocumentProperty>().Where(p => p.Name?.Value == "LOPATA.SourceProvenance").ToArray()) old.Remove();
                var id = part.Properties.Elements<CustomDocumentProperty>().Select(p => p.PropertyId?.Value ?? 1).DefaultIfEmpty(1).Max() + 1;
                part.Properties.Append(new CustomDocumentProperty(new VTLPWSTR(json)) { Name = "LOPATA.SourceProvenance", FormatId = "{D5CDD505-2E9C-101B-9397-08002B2CF9AE}", PropertyId = id }); part.Properties.Save(); return;
            }
            using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
            if (format == "cbz") { Replace(zip, "LOPATA.SourceProvenance.json", json); return; }
            var name = format == "odt" ? "meta.xml" : zip.Entries.First(e => e.FullName.EndsWith(".opf", StringComparison.OrdinalIgnoreCase)).FullName;
            XDocument xml; using (var stream = zip.GetEntry(name)!.Open()) xml = BookMarkup.Xml(stream);
            if (format == "odt")
            {
                XNamespace office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0", meta = "urn:oasis:names:tc:opendocument:xmlns:meta:1.0";
                var container = xml.Root!.Element(office + "meta")!; container.Add(new XElement(meta + "user-defined", new XAttribute(meta + "name", "LOPATA.SourceProvenance"), new XAttribute(meta + "value-type", "string"), json));
            }
            else
            {
                XNamespace opf = "http://www.idpf.org/2007/opf"; var metadata = xml.Root!.Element(opf + "metadata")!;
                var is2 = xml.Root.Attribute("version")?.Value.StartsWith('2') == true;
                if (is2) metadata.Add(new XElement(opf + "meta", new XAttribute("name", "LOPATA.SourceProvenance"), new XAttribute("content", json)));
                else
                {
                    var prefix = xml.Root.Attribute("prefix")?.Value ?? "";
                    if (!prefix.Contains("lopata:")) xml.Root.SetAttributeValue("prefix", prefix + " lopata: https://lopata.app/ns/provenance/1.0/");
                    metadata.Add(new XElement(opf + "meta", new XAttribute("property", "lopata:source-provenance"), json));
                }
            }
            Replace(zip, name, xml.ToString()); return;
        }
        if (format == "fb2")
        {
            using var input = File.OpenRead(path); var xml = BookMarkup.Xml(input); input.Dispose(); XNamespace ns = "http://www.gribuser.ru/xml/fictionbook/2.0";
            xml.Root!.Element(ns + "description")!.Add(new XElement(ns + "custom-info", new XAttribute("info-type", "LOPATA.SourceProvenance"), json)); xml.Save(path);
        }
        else if (format == "html")
        {
            var doc = BookMarkup.Parse(File.ReadAllText(path)); var meta = doc.CreateElement("meta"); meta.SetAttribute("name", "LOPATA.SourceProvenance"); meta.SetAttribute("content", json); doc.Head!.AppendChild(meta); File.WriteAllText(path, "<!DOCTYPE html>" + doc.DocumentElement.OuterHtml, new UTF8Encoding(false));
        }
        else if (format is "txt" or "md") File.WriteAllText(path, (format == "md" ? "<!-- LOPATA.SourceProvenance=" + json + " -->" : "[LOPATA.SourceProvenance=" + json + "]") + "\n\n" + File.ReadAllText(path), new UTF8Encoding(false));
    }
    private static void Replace(ZipArchive zip, string name, string contents) { zip.GetEntry(name)?.Delete(); using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(contents); }
}
