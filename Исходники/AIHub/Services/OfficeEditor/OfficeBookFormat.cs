using System.IO;
using System.Xml.Linq;

namespace AIHub.Services.OfficeEditor;

internal static partial class OfficeBookFormat
{
    private static readonly XNamespace Fb = "http://www.gribuser.ru/xml/fictionbook/2.0";
    private static readonly XNamespace Html = "http://www.w3.org/1999/xhtml";
    private static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace Container = "urn:oasis:names:tc:opendocument:xmlns:container";
    private static readonly XNamespace Ops = "http://www.idpf.org/2007/ops";
    private static readonly XNamespace Ncx = "http://www.daisy.org/z3986/2005/ncx/";
    private static InvalidDataException InvalidBook() => new("OfficeEditor.InvalidPackage");

    internal static IReadOnlyList<OfficeNativePage> Read(OfficeNativePackage package, string extension) =>
        extension switch
        {
            ".epub" => ReadEpub(package),
            ".fb2" => ReadFb2(package.Xml("")),
            _ => throw new InvalidDataException("OfficeEditor.Unsupported")
        };

    internal static bool IsProtected(OfficeNativePackage package, string extension)
    {
        if (extension == ".fb2")
            return package.Xml("").Descendants(XName.Get("Signature", "http://www.w3.org/2000/09/xmldsig#")).Any();
        if (extension != ".epub") return false;
        if (package.Entries.Contains("META-INF/signatures.xml", StringComparer.Ordinal)) return true;
        if (!package.Entries.Contains("META-INF/encryption.xml", StringComparer.Ordinal)) return false;
        XNamespace enc = "http://www.w3.org/2001/04/xmlenc#";
        var encrypted = package.Xml("META-INF/encryption.xml").Descendants(enc + "EncryptedData").ToArray();
        // Obfuscated fonts are opaque resources in the original package. Any
        // other encryption scheme is left to the reader and its rights handler.
        if (encrypted.Length == 0) return true;
        foreach (var item in encrypted)
        {
            var method = (string?)item.Element(enc + "EncryptionMethod")?.Attribute("Algorithm");
            var reference = (string?)item.Element(enc + "CipherData")?.Element(enc + "CipherReference")?.Attribute("URI");
            if (method is not ("http://www.idpf.org/2008/embedding" or "http://ns.adobe.com/pdf/enc#RC")
                || string.IsNullOrEmpty(reference)) return true;
            if (Path.GetExtension(reference).ToLowerInvariant() is not (".otf" or ".ttf" or ".woff" or ".woff2")) return true;
        }
        return false;
    }

    private static IReadOnlyList<OfficeNativePage> ReadFb2(XDocument document)
    {
        var root = document.Root;
        if (root?.Name != Fb + "FictionBook") throw InvalidBook();
        var pages = new List<OfficeNativePage>();
        foreach (var body in root.Elements(Fb + "body"))
        {
            // Register sections in reading order, including nested chapters and
            // the notes body. A paragraph belongs only to its closest section.
            var groups = body.DescendantsAndSelf().Where(x => x == body || x.Name == Fb + "section")
                .ToDictionary(x => x, _ => new List<Passage>());
            foreach (var element in body.Descendants().Where(IsFbParagraph))
            {
                var group = element.Ancestors().First(groups.ContainsKey);
                groups[group].Add(new Passage(element, element.DescendantNodes().OfType<XText>().ToList()));
            }
            foreach (var (group, passages) in groups)
            {
                if (group == body && passages.Count == 0) continue;
                var heading = group.Element(Fb + "title");
                var number = (pages.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var fallback = (string?)group.Attribute("id") ?? (string?)group.Attribute("name") ?? number;
                string Title()
                {
                    var name = string.Join(" ",heading?.Elements(Fb + "p").Select(p => p.Value.Trim()) ?? []);
                    return string.IsNullOrWhiteSpace(name) ? fallback : name;
                }
                pages.Add(new OfficeNativePage(Title, passages.Select(p => new OfficeNativeBlock(() => p.Text, p.Write,
                    allowLineBreaks: p.Element.Name.LocalName is "p" or "v")).ToArray()));
            }
        }
        if (pages.Count == 0) throw InvalidBook();
        return pages;
    }

    private static bool IsFbParagraph(XElement node) => node.Name.Namespace == Fb &&
        node.Name.LocalName is "p" or "v" or "subtitle" or "text-author" or "th" or "td";
}
