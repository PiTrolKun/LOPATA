using System.Globalization;
using System.Xml.Linq;

namespace AIHub.Services.OfficeEditor;

internal static partial class OfficeSlideFormat
{
    private static IReadOnlyList<OfficeNativePage> ReadOdp(OfficeNativePackage package)
    {
        var content = package.Xml("content.xml").Root ?? throw Invalid();
        var slides = content.Element(Office + "body")?.Element(Office + "presentation") ?? throw Invalid();
        var styles = new SlideOdfStyles(content,
            package.Entries.Contains("styles.xml", StringComparer.Ordinal) ? package.Xml("styles.xml").Root : null);
        var pages = new List<OfficeNativePage>();
        foreach (var page in slides.Elements(Draw + "page"))
        {
            var blocks = new List<OfficeNativeBlock>();
            OfficeNativeBlock? heading = null;
            foreach (var paragraph in page.Descendants().Where(e => e.Name == OText + "p" || e.Name == OText + "h"))
            {
                var ancestors = paragraph.Ancestors().TakeWhile(e => e != page).ToArray();
                if (ancestors.Any(e => e.Name == Pres + "notes" || e.Name == Office + "annotation" ||
                    e.Name == OText + "note" || e.Name == Draw + "object" || e.Name == Draw + "object-ole")) continue;
                var shape = ancestors.FirstOrDefault(e => e.Name == Draw + "custom-shape");
                if (shape != null && !shape.Descendants().Where(e => e.Name == OText + "p" || e.Name == OText + "h")
                    .Any(e => !string.IsNullOrWhiteSpace(e.Value))) continue;
                var isTitle = ancestors.Any(e => (string?)e.Attribute(Pres + "class") == "title");
                foreach (var block in OdpParagraph(paragraph, styles))
                {
                    if (isTitle) heading ??= block;
                    blocks.Add(block);
                }
            }
            pages.Add(new(() => heading?.Text ?? blocks.FirstOrDefault(b => !string.IsNullOrWhiteSpace(b.Text))?.Text
                ?? (string?)page.Attribute(Draw + "name") ?? "", blocks));
        }
        return pages;
    }

    private static IEnumerable<XNode?> OdpFlow(XElement parent)
    {
        foreach (var child in parent.Nodes())
        {
            if (child is XText t)
            {
                if (t.Value.Contains('\n') && string.IsNullOrWhiteSpace(t.Value)) continue;
                yield return t;
            }
            else if (child is XElement e)
            {
                if (e.Name == OText + "s" || e.Name == OText + "line-break" || e.Name == OText + "tab") yield return e;
                else if (e.Name == OText + "span" || e.Name == OText + "a")
                {
                    foreach (var item in OdpFlow(e)) yield return item;
                }
                else yield return null; // An automatic field or embedded object is an immutable boundary.
            }
        }
    }

    private static IEnumerable<OfficeNativeBlock> OdpParagraph(XElement paragraph, SlideOdfStyles styles)
    {
        var current = new List<XNode>(); bool hasContent = false;
        foreach (var node in OdpFlow(paragraph))
        {
            hasContent = true;
            if (node != null) current.Add(node);
            else if (current.Count > 0) { yield return Block(current); current = []; }
        }
        if (current.Count > 0) yield return Block(current);
        if (!hasContent) yield return Block([new XText("")]);

        OfficeNativeBlock Block(List<XNode> parts)
        {
            var anchor = paragraph.Descendants(OText + "span").LastOrDefault(e => !e.HasElements && e.Value.Length == 0) ?? paragraph;
            void Attach(XNode node) { if (node.Parent == null) anchor.Add(node); }
            IEnumerable<XNode> Replace(XNode previous, string value)
            {
                Attach(previous);
                var next = OdfTextNodes(value).ToList();
                previous.ReplaceWith(next);
                return next;
            }
            var text = new SlideText<XNode>(parts, ReadOdfToken, Replace);
            bool Has(XName key, string desired) => text.Parts.All(n =>
            {
                var attribute = styles.Value(n.Parent ?? anchor, key);
                return attribute == desired || key == Fo + "font-weight" && int.TryParse(attribute, out var weight) && weight >= 600
                    || key == Fo + "font-style" && attribute == "oblique";
            });
            void Set(XName key, string value)
            {
                foreach (var node in text.Parts) Attach(node);
                styles.Apply(text.Parts, key, value);
            }
            return new(() => text.Text, text.Write,
                () => Has(Fo + "font-weight", "bold"), value => Set(Fo + "font-weight", value ? "bold" : "normal"),
                () => Has(Fo + "font-style", "italic"), value => Set(Fo + "font-style", value ? "italic" : "normal"),
                () => CommonSize(text.Parts.Select(n => OdfPoints(styles.Value(n.Parent ?? anchor, Fo + "font-size")))),
                size => Set(Fo + "font-size", FontPoints(size).ToString(CultureInfo.InvariantCulture) + "pt"));
        }
    }

    private static double? OdfPoints(string? value)
    {
        if (value?.Length < 3 || value == null || !double.TryParse(value[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return null;
        return value[^2..] switch { "pt" => n, "pc" => 12 * n, "in" => 72 * n, "cm" => 72 * n / 2.54, "mm" => 72 * n / 25.4, _ => null };
    }

    private static string ReadOdfToken(XNode node)
    {
        if (node is XText text) return text.Value;
        var e = (XElement)node;
        if (e.Name == OText + "line-break") return "\n";
        if (e.Name == OText + "tab") return "\t";
        var count = (int?)e.Attribute(OText + "c") ?? 1;
        if (count is < 1 or > 1_000_000) throw Invalid();
        return new string(' ', count);
    }

    private static IEnumerable<XNode> OdfTextNodes(string value)
    {
        if (value.Length == 0) yield return new XText("");
        for (var i = 0; i < value.Length;)
        {
            var start = i++;
            switch (value[start])
            {
                case '\n': yield return new XElement(OText + "line-break"); break;
                case '\t': yield return new XElement(OText + "tab"); break;
                case ' ':
                    while (i < value.Length && value[i] == ' ') i++;
                    yield return new XElement(OText + "s", i - start > 1 ? new XAttribute(OText + "c", i - start) : null);
                    break;
                default:
                    while (i < value.Length && value[i] is not ('\n' or '\t' or ' ')) i++;
                    yield return new XText(value[start..i]); break;
            }
        }
    }

    private sealed class SlideOdfStyles
    {
        private readonly XElement _document;
        private readonly Dictionary<(string Family, string Name), XElement> _styles = [];
        private readonly Dictionary<string, string> _combinations = [];
        private readonly HashSet<XElement> _localWrappers = [];
        private int _next;
        internal SlideOdfStyles(XElement document, XElement? styleDocument)
        {
            _document = document;
            foreach (var root in new[] { styleDocument, document })
            foreach (var group in root?.Elements().Where(e => e.Name == Office + "styles" || e.Name == Office + "automatic-styles") ?? [])
            foreach (var style in group.Elements())
            {
                if (style.Name != Style + "style" && style.Name != Style + "default-style") continue;
                _styles[((string?)style.Attribute(Style + "family") ?? "", (string?)style.Attribute(Style + "name") ?? "")] = style;
            }
        }

        private string? ResolveStyle(string family, string? name, XName key)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (name != null && visited.Add(name) && _styles.TryGetValue((family, name), out var style))
            {
                if ((string?)style.Element(Style + "text-properties")?.Attribute(key) is { } val) return val;
                name = (string?)style.Attribute(Style + "parent-style-name");
            }
            return null;
        }

        internal string? Value(XElement element, XName key)
        {
            foreach (var e in element.AncestorsAndSelf())
            {
                var style = (string?)e.Attribute(OText + "style-name") ?? (string?)e.Attribute(Draw + "text-style-name");
                var family = e.Name == OText + "a" || e.Name == OText + "span" ? "text" : "paragraph";
                if (ResolveStyle(family, style, key) is { } val) return val;
            }
            return ResolveStyle("paragraph", "", key);
        }

        internal void Apply(IReadOnlyList<XNode> nodes, XName key, string value)
        {
            var parents = nodes.GroupBy(n => n.Parent!).ToList();
            foreach (var group in parents)
            {
                var parent = group.Key;
                if (_localWrappers.Contains(parent) && parent.Nodes().All(group.Contains))
                {
                    var previous = (string?)parent.Attribute(OText + "style-name");
                    parent.SetAttributeValue(OText + "style-name", Find(previous, key, value));
                    continue;
                }
                foreach (var node in group)
                {
                    var wrapper = new XElement(OText + "span", new XAttribute(OText + "style-name", Find(null, key, value)));
                    node.ReplaceWith(wrapper); wrapper.Add(node); _localWrappers.Add(wrapper);
                }
            }
        }

        private string Find(string? previous, XName property, string value)
        {
            var props = previous != null && _styles.TryGetValue(("text", previous), out var old)
                ? new XElement(old.Element(Style + "text-properties")!) : new XElement(Style + "text-properties");
            props.SetAttributeValue(property, value);
            var signature = string.Join("|", props.Attributes().OrderBy(a => a.Name.LocalName).Select(a => a.Name + "=" + a.Value));
            if (_combinations.TryGetValue(signature, out var cached)) return cached;
            string name;
            do { name = "LopataRun" + ++_next; } while (_styles.ContainsKey(("text", name)));
            var style = new XElement(Style + "style", new XAttribute(Style + "family", "text"), new XAttribute(Style + "name", name), props);
            var group = _document.Element(Office + "automatic-styles");
            if (group == null)
            {
                group = new XElement(Office + "automatic-styles");
                _document.Element(Office + "body")!.AddBeforeSelf(group);
            }
            group.Add(style); _styles[("text", name)] = style; _combinations[signature] = name;
            return name;
        }
    }
}
