using System.Globalization;
using System.IO;
using System.Xml.Linq;

namespace AIHub.Services.OfficeEditor;

internal static partial class OfficeBookFormat
{
    private sealed record EpubParts(string Path, XElement Root, XElement Manifest, XElement Spine);

    private static EpubParts Publication(OfficeNativePackage package)
    {
        var path = (string?)package.Xml("META-INF/container.xml").Descendants(Container + "rootfile")
            .FirstOrDefault(e => (string?)e.Attribute("media-type") == "application/oebps-package+xml")?.Attribute("full-path")
            ?? throw InvalidBook();
        path = OfficeNativePackage.Resolve("",path);
        var root = package.Xml(path).Root;
        if (root?.Name != Opf + "package") throw InvalidBook();
        return new(path,root,root.Element(Opf + "manifest") ?? throw InvalidBook(),root.Element(Opf + "spine") ?? throw InvalidBook());
    }

    internal static bool CanAppend(OfficeNativePackage package,string extension)
    {
        if (IsProtected(package,extension)) return false;
        if (extension == ".fb2") return package.Xml("").Root?.Element(Fb + "body") != null;
        if (extension != ".epub") return false;
        var book = Publication(package);
        // A rendition with a different base URL at individual elements needs
        // a corresponding URL rewriter before we can safely extend its spine.
        if (book.Root.DescendantsAndSelf().Attributes(XNamespace.Xml + "base").Any()) return false;
        return ((string?)book.Root.Attribute("version"))?.Split('.')[0] is "2" or "3";
    }

    internal static void Append(OfficeNativePackage package,string extension,string title)
    {
        if (!CanAppend(package,extension)) throw new InvalidDataException("OfficeEditor.Unsupported");
        var key = "lopata-" + Guid.NewGuid().ToString("N");
        if (extension == ".fb2")
        {
            package.Xml("").Root!.Element(Fb + "body")!.Add(new XElement(Fb + "section",new XAttribute("id",key),
                new XElement(Fb + "title",new XElement(Fb + "p",title)),new XElement(Fb + "p")));
            return;
        }
        var book = Publication(package);
        var items = book.Manifest.Elements(Opf + "item").ToDictionary(e => (string)e.Attribute("id")!,StringComparer.Ordinal);
        var previous = book.Spine.Elements(Opf + "itemref").Reverse().Select(r => items[(string)r.Attribute("idref")!])
            .FirstOrDefault(item => (string?)item.Attribute("media-type") == "application/xhtml+xml" &&
                !IsNavigationItem(item) && item.Attribute("href") is { } href && IsLocalReference(href.Value));
        var lastPart = previous == null ? book.Path : OfficeNativePackage.Resolve(book.Path,(string)previous.Attribute("href")!);
        var path = OfficeNativePackage.Resolve(lastPart,key + ".xhtml");
        // div belongs to both EPUB 2's XHTML 1.1 and EPUB 3's HTML vocabulary.
        var html = HtmlDocument(title,new XElement(Html + "div",new XElement(Html + "h1",title),new XElement(Html + "p")));
        if (previous != null)
        {
            var prior = package.Xml(lastPart).Root;
            var links = prior?.Element(Html + "head")?.Elements(Html + "link")
                .Where(e => ((string?)e.Attribute("rel") ?? "").Split(' ').Contains("stylesheet",StringComparer.OrdinalIgnoreCase)
                    && e.Attribute("href") is { } href && IsLocalReference(href.Value));
            if (links != null) html.Element(Html + "head")!.Add(links.Select(e => new XElement(e)));
            foreach (var language in new[] {(XName)"lang", XNamespace.Xml + "lang"})
                if (prior?.Attribute(language) is { } lang) html.SetAttributeValue(language,lang.Value);
        }
        // Prepare all navigation operations before mutating the package.
        var addLabels = NavigationAppends(package,book,items.Values,path,key,title);
        package.AddXml(path,new XDocument(new XDeclaration("1.0","utf-8",null),html));
        book.Manifest.Add(new XElement(Opf + "item",new XAttribute("id",key),new XAttribute("href",RelativeTo(book.Path,path)),
            new XAttribute("media-type","application/xhtml+xml")));
        book.Spine.Add(new XElement(Opf + "itemref",new XAttribute("idref",key)));
        foreach (var add in addLabels) add();
    }

    private static bool IsNavigationItem(XElement item) =>
        ((string?)item.Attribute("properties") ?? "").Split(' ',StringSplitOptions.RemoveEmptyEntries).Contains("nav");

    private static string RelativeTo(string from,string to)
    {
        var directory = from.Split('/')[..^1];
        var target = to.Split('/');
        var i = 0;
        while (i < directory.Length && i < target.Length - 1 && directory[i] == target[i]) i++;
        return string.Join("/",Enumerable.Repeat("..", directory.Length - i).Concat(target.Skip(i).Select(Uri.EscapeDataString)));
    }

    private static List<Action> NavigationAppends(OfficeNativePackage package,EpubParts book,
        IEnumerable<XElement> items,string newPath,string key,string label)
    {
        var actions = new List<Action>();
        var guides = new Dictionary<string,string>(StringComparer.Ordinal);
        foreach (var reference in book.Root.Element(Opf + "guide")?.Elements(Opf + "reference") ?? [])
        {
            var href = (string?)reference.Attribute("href");
            if ((string?)reference.Attribute("type") != "toc" || href == null || !IsLocalReference(href)) continue;
            guides[OfficeNativePackage.Resolve(book.Path,href)] = Uri.UnescapeDataString(href.Split('#').ElementAtOrDefault(1) ?? "");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var href = (string?)item.Attribute("href");
            if (href == null || !IsLocalReference(href)) continue;
            var path = OfficeNativePackage.Resolve(book.Path,href);
            if (!seen.Add(path)) continue;
            var target = RelativeTo(path,newPath);
            if ((string?)item.Attribute("media-type") == "application/x-dtbncx+xml")
            {
                var ncx = package.Xml(path);
                var map = ncx.Root?.Element(Ncx + "navMap") ?? throw InvalidBook();
                var order = ncx.Descendants().Attributes("playOrder")
                    .Select(a => long.TryParse(a.Value,NumberStyles.None,CultureInfo.InvariantCulture,out var n) ? n : 0)
                    .DefaultIfEmpty().Max() + 1;
                var point = new XElement(Ncx + "navPoint",new XAttribute("id",key),new XAttribute("playOrder",order),
                    new XElement(Ncx + "navLabel",new XElement(Ncx + "text",label)),new XElement(Ncx + "content",new XAttribute("src",target)));
                actions.Add(() => map.Add(point));
            }
            else if (IsNavigationItem(item) || guides.ContainsKey(path))
            {
                var doc = package.Xml(path);
                var nav = doc.Descendants(Html + "nav").FirstOrDefault(n => ((string?)n.Attribute(Ops + "type") ?? "").Split(' ').Contains("toc"));
                if (nav == null && guides.TryGetValue(path,out var fragment) && fragment.Length != 0)
                    nav = doc.Descendants().FirstOrDefault(n => (string?)n.Attribute("id") == fragment);
                nav ??= doc.Root?.Element(Html + "body") ?? throw InvalidBook();
                var list = nav.DescendantsAndSelf().FirstOrDefault(n => n.Name == Html + "ol" || n.Name == Html + "ul");
                var link = new XElement(Html + "a",new XAttribute("href",target),label);
                actions.Add(list == null ? () => nav.Add(new XElement(Html + "p",link))
                    : () => list.Add(new XElement(Html + "li",link)));
            }
        }
        return actions;
    }
}
