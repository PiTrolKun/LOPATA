using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace AIHub.Services.OfficeEditor;

internal static partial class OfficeBookFormat
{
    internal static byte[] Create(string extension, IReadOnlyList<OfficeSheetSection> sections)
    {
        if (sections.Count == 0) throw InvalidBook();
        var id = "urn:uuid:" + Guid.NewGuid().ToString();
        var title = sections.Select(section => section.Title).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? "LOPATA";
        return extension switch
        {
            ".fb2" => CreateFb2(sections, title, id),
            ".epub" => CreateEpub(sections, title, id),
            _ => throw new InvalidDataException("OfficeEditor.Unsupported")
        };
    }

    private static XElement[] Paragraphs(OfficeSheetSection section, XNamespace ns)
        => section.Body.ReplaceLineEndings("\n").Split('\n').Select(line => new XElement(ns + "p", line)).ToArray();

    private static void WriteBookXml(Stream output, XElement root)
    {
        using var writer = XmlWriter.Create(output, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false });
        new XDocument(new XDeclaration("1.0", "utf-8", null), root).Save(writer);
    }

    private static byte[] CreateFb2(IReadOnlyList<OfficeSheetSection> sections, string title, string id)
    {
        var date = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        XElement EmptyAuthor() => new(Fb + "author", new XElement(Fb + "nickname"));
        var root = new XElement(Fb + "FictionBook",
            new XElement(Fb + "description",
                new XElement(Fb + "title-info", new XElement(Fb + "genre", "unrecognised"), EmptyAuthor(),
                    new XElement(Fb + "book-title", title), new XElement(Fb + "lang", "und")),
                new XElement(Fb + "document-info", EmptyAuthor(), new XElement(Fb + "program-used", "LOPATA"),
                    new XElement(Fb + "date", new XAttribute("value", date), date), new XElement(Fb + "id", id),
                    new XElement(Fb + "version", "1.0"))),
            new XElement(Fb + "body", sections.Select((section,index) =>
                new XElement(Fb + "section", new XAttribute("id", $"chapter-{index + 1}"),
                    new XElement(Fb + "title", new XElement(Fb + "p", section.Title)), Paragraphs(section,Fb)))));
        using var output = new MemoryStream();
        WriteBookXml(output,root);
        return output.ToArray();
    }

    private static byte[] CreateEpub(IReadOnlyList<OfficeSheetSection> sections, string title, string id)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            using (var mime = archive.CreateEntry("mimetype",CompressionLevel.NoCompression).Open())
                mime.Write("application/epub+zip"u8);
            void AddXml(string path, XElement xml)
            {
                using var target = archive.CreateEntry(path,CompressionLevel.Optimal).Open();
                WriteBookXml(target,xml);
            }
            AddXml("META-INF/container.xml", new XElement(Container + "container", new XAttribute("version","1.0"),
                new XElement(Container + "rootfiles",new XElement(Container + "rootfile",new XAttribute("full-path","EPUB/content.opf"),
                    new XAttribute("media-type","application/oebps-package+xml")))));
            XNamespace dc = "http://purl.org/dc/elements/1.1/";
            var manifest = new XElement(Opf + "manifest",new XElement(Opf + "item",new XAttribute("id","nav"),
                new XAttribute("href","nav.xhtml"),new XAttribute("media-type","application/xhtml+xml"),new XAttribute("properties","nav")));
            var spine = new XElement(Opf + "spine");
            var contents = new XElement(Html + "ol");
            for (var index = 0; index < sections.Count; index++)
            {
                var section = sections[index];
                var key = $"chapter-{index + 1}";
                var href = key + ".xhtml";
                manifest.Add(new XElement(Opf + "item",new XAttribute("id",key),new XAttribute("href",href),
                    new XAttribute("media-type","application/xhtml+xml")));
                spine.Add(new XElement(Opf + "itemref",new XAttribute("idref",key)));
                var label = string.IsNullOrWhiteSpace(section.Title) ? (index + 1).ToString(CultureInfo.InvariantCulture) : section.Title;
                contents.Add(new XElement(Html + "li",new XElement(Html + "a",new XAttribute("href",href),label)));
                AddXml("EPUB/" + href, HtmlDocument(label,
                    new XElement(Html + "section",new XElement(Html + "h1",section.Title),Paragraphs(section,Html))));
            }
            AddXml("EPUB/nav.xhtml", HtmlDocument(title, new XElement(Html + "nav",
                new XAttribute(XNamespace.Xmlns + "epub",Ops),new XAttribute(Ops + "type","toc"),
                new XElement(Html + "h1",title),contents)));
            AddXml("EPUB/content.opf", new XElement(Opf + "package",new XAttribute("version","3.0"),
                new XAttribute("unique-identifier","book-id"),new XElement(Opf + "metadata",new XAttribute(XNamespace.Xmlns + "dc",dc),
                    new XElement(dc + "identifier",new XAttribute("id","book-id"),id),new XElement(dc + "title",title),
                    new XElement(dc + "language","und"),new XElement(Opf + "meta",new XAttribute("property","dcterms:modified"),
                        DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'",CultureInfo.InvariantCulture))),manifest,spine));
        }
        return output.ToArray();
    }

    private static XElement HtmlDocument(string title, XElement section) => new(Html + "html",
        new XAttribute("lang","und"),new XElement(Html + "head",new XElement(Html + "title",title)),
        new XElement(Html + "body",section));
}
