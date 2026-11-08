using System.IO;
using System.Xml.Linq;

namespace AIHub.Services.OfficeEditor;

internal static partial class OfficeBookFormat
{
    private sealed record NavigationLabel(string Fragment, Passage Label);

    private static IReadOnlyList<OfficeNativePage> ReadEpub(OfficeNativePackage package)
    {
        var publication = Publication(package);
        var (packageName,opf,manifest,spine) = publication;
        var items = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var item in manifest.Elements(Opf + "item"))
            if ((string?)item.Attribute("id") is not { Length: > 0 } id || !items.TryAdd(id, item)) throw InvalidBook();
        var labels = ReadNavigation(package, packageName, opf, items.Values);
        var pages = new List<OfficeNativePage>();
        foreach (var reference in spine.Elements(Opf + "itemref"))
        {
            var id = (string?)reference.Attribute("idref") ?? throw InvalidBook();
            if (!items.TryGetValue(id, out var item)) throw InvalidBook();
            var href = (string?)item.Attribute("href") ?? throw InvalidBook();
            if ((string?)item.Attribute("media-type") != "application/xhtml+xml" || !IsLocalReference(href))
            {
                pages.Add(new OfficeNativePage(() => id, []));
                continue;
            }
            var path = OfficeNativePackage.Resolve(packageName, href);
            var html = package.Xml(path).Root;
            var body = html?.Element(Html + "body") ?? throw InvalidBook();
            var title = html!.Element(Html + "head")?.Element(Html + "title");
            var passages = HtmlPassages(body);
            // A single spine item can contain the book title at h1 and several
            // chapters at h2. Honor chapter anchors in the actual table of contents.
            var headings = passages.Select(p => p.Element).Where(e => HeadingRank(e) < 7).Distinct().ToArray();
            var topRank = headings.Select(HeadingRank).DefaultIfEmpty(7).Min();
            var starts = headings.Where(h => HeadingRank(h) == topRank).ToHashSet();
            if (labels.TryGetValue(path,out var chapterLinks))
            {
                var remaining = chapterLinks.Select(link => link.Fragment).Where(f => f.Length != 0).ToHashSet(StringComparer.Ordinal);
                foreach (var heading in headings)
                    foreach (var anchor in heading.AncestorsAndSelf().Attributes("id"))
                        if (remaining.Remove(anchor.Value)) starts.Add(heading);
            }
            var sections = new List<List<Passage>> { new() };
            XElement? lastHeading = null;
            foreach (var passage in passages)
            {
                if (starts.Contains(passage.Element) && passage.Element != lastHeading)
                {
                    if (sections[^1].Count != 0) sections.Add([]);
                    lastHeading = passage.Element;
                }
                sections[^1].Add(passage);
            }
            var documentHeading = headings.FirstOrDefault();
            foreach (var section in sections)
            {
                var heading = section.FirstOrDefault(p => starts.Contains(p.Element))?.Element;
                var titleRuns = section.Where(p => p.Element == heading).ToList();
                string HeadingText() => string.Join(" ",titleRuns.Select(p => p.Text.ReplaceLineEndings(" ").Trim())).Trim();
                string PageTitle() => HeadingText() is { Length: > 0 } name ? name :
                    !string.IsNullOrWhiteSpace(title?.Value) ? title.Value.Trim() : Path.GetFileNameWithoutExtension(path);
                var blocks = new List<OfficeNativeBlock>();
                foreach (var passage in section)
                {
                    if (heading != passage.Element) { blocks.Add(new(() => passage.Text, passage.Write)); continue; }
                    var anchors = heading.AncestorsAndSelf().Attributes("id").Select(a => a.Value).ToHashSet(StringComparer.Ordinal);
                    var firstHeading = heading == documentHeading;
                    blocks.Add(new(() => passage.Text, value =>
                    {
                        var old = HeadingText();
                        passage.Write(value);
                        var newTitle = HeadingText();
                        if (firstHeading && title != null && SameLabel(title.Value, old)) title.Value = newTitle;
                        if (!labels.TryGetValue(path, out var linked)) return;
                        foreach (var target in linked)
                            if ((target.Fragment.Length == 0 ? firstHeading : anchors.Contains(target.Fragment))
                                && SameLabel(target.Label.Text, old)) target.Label.Write(newTitle);
                    }));
                }
                pages.Add(new OfficeNativePage(PageTitle, blocks));
            }
        }
        if (pages.Count == 0) throw InvalidBook();
        return pages;
    }

    private static bool IsLocalReference(string href) => !href.StartsWith("//", StringComparison.Ordinal)
        && (href.StartsWith('/') || !Uri.TryCreate(href, UriKind.Absolute, out _));

    private static bool SameLabel(string left, string right) => string.Equals(left.Trim(), right.Trim(), StringComparison.Ordinal);

    private static Dictionary<string,List<NavigationLabel>> ReadNavigation(OfficeNativePackage package, string packageName,
        XElement opf, IEnumerable<XElement> items)
    {
        var labels = new Dictionary<string,List<NavigationLabel>>(StringComparer.Ordinal);
        var contents = new HashSet<string>(StringComparer.Ordinal);
        foreach (var guide in opf.Element(Opf + "guide")?.Elements(Opf + "reference") ?? [])
            if ((string?)guide.Attribute("type") == "toc" && (string?)guide.Attribute("href") is { } href && IsLocalReference(href))
                contents.Add(OfficeNativePackage.Resolve(packageName, href));
        void Add(string source, string? href, XElement? label)
        {
            if (string.IsNullOrWhiteSpace(href) || label == null || !IsLocalReference(href)) return;
            var hash = href.IndexOf('#');
            var key = hash == 0 ? source : OfficeNativePackage.Resolve(source, href);
            var fragment = hash < 0 ? "" : Uri.UnescapeDataString(href[(hash+1)..]);
            if (!labels.TryGetValue(key, out var linked)) labels[key] = linked = [];
            linked.Add(new(fragment, new(label, label.DescendantNodes().OfType<XText>().ToList())));
        }
        foreach (var item in items)
        {
            var href = (string?)item.Attribute("href");
            if (href == null || !IsLocalReference(href)) continue;
            var type = (string?)item.Attribute("media-type");
            if (type is not ("application/xhtml+xml" or "application/x-dtbncx+xml")) continue;
            var path = OfficeNativePackage.Resolve(packageName, href);
            if (type == "application/x-dtbncx+xml")
            {
                foreach (var point in package.Xml(path).Descendants(Ncx + "navPoint"))
                    Add(path, (string?)point.Element(Ncx + "content")?.Attribute("src"), point.Element(Ncx + "navLabel")?.Element(Ncx + "text"));
            }
            else if (((string?)item.Attribute("properties") ?? "").Split(' ').Contains("nav") || contents.Contains(path))
            {
                var doc = package.Xml(path);
                var toc = doc.Descendants(Html + "nav").FirstOrDefault(nav => ((string?)nav.Attribute(Ops + "type") ?? "").Split(' ').Contains("toc"));
                foreach (var link in (toc ?? doc.Root!).Descendants(Html + "a"))
                    Add(path, (string?)link.Attribute("href"), link);
            }
        }
        return labels;
    }

    private static int HeadingRank(XElement element) => element.Name.LocalName is "h1" or "h2" or "h3" or "h4" or "h5" or "h6"
        ? element.Name.LocalName[1] - '0' : 7;

    private static List<Passage> HtmlPassages(XElement body)
    {
        var result = new List<Passage>();
        var pending = new List<XText>();
        void Flush(XElement owner)
        {
            if (pending.Any(t => !string.IsNullOrWhiteSpace(t.Value))) result.Add(new(owner, pending));
            pending = [];
        }
        void Walk(XElement element, XElement owner, int depth)
        {
            if (depth > 256) throw InvalidBook();
            foreach (var node in element.Nodes())
            {
                if (node is XText t) { pending.Add(t); continue; }
                if (node is not XElement child || child.Name.Namespace != Html ||
                    child.Name.LocalName is "script" or "style" or "template" or "iframe" or "object" or "noscript") continue;
                var name = child.Name.LocalName;
                if (name == "br") { Flush(owner); continue; }
                if (HeadingRank(child) < 7 || name is "p" or "div" or "section" or "article" or "aside" or "nav" or "li"
                    or "header" or "footer" or "blockquote" or "pre" or "td" or "th" or "dt" or "dd" or "figure" or "figcaption")
                {
                    Flush(owner);
                    Walk(child, child, depth + 1);
                    Flush(child);
                    if ((name == "p" || HeadingRank(child) < 7) && !child.Nodes().Any()) result.Add(new(child, []));
                }
                else Walk(child, owner, depth + 1);
            }
        }
        Walk(body, body, 0);
        Flush(body);
        return result;
    }
}
