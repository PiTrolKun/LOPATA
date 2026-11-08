using System.Globalization;
using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace AIHub.Services.OfficeEditor;

internal static partial class OfficeSlideFormat
{
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace Office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private static readonly XNamespace Draw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    private static readonly XNamespace OText = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private static readonly XNamespace Style = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
    private static readonly XNamespace Fo = "urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0";
    private static readonly XNamespace Svg = "urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0";
    private static readonly XNamespace Pres = "urn:oasis:names:tc:opendocument:xmlns:presentation:1.0";

    internal static IReadOnlyList<OfficeNativePage> Read(OfficeNativePackage package, string extension)
        => extension switch
        {
            ".pptx" => ReadPptx(package),
            ".odp" => ReadOdp(package),
            _ => throw new InvalidDataException("OfficeEditor.Unsupported")
        };

    internal static byte[] Create(string extension, IReadOnlyList<OfficeSheetSection> sections)
    {
        if (sections.Count == 0) sections = [new OfficeSheetSection()];
        var bytes = extension switch
        {
            ".pptx" => CreatePptx(sections),
            ".odp" => CreateOdp(sections),
            _ => throw new InvalidDataException("OfficeEditor.Unsupported")
        };
        if (!sections.Any(s => s.HideTitle || s.HideBody || s.TitleAngle != 0 || s.BodyAngle != 0 || s.Shapes.Count > 0 || s.ObjectOrder.Count > 0)) return bytes;
        var package = new OfficeNativePackage(bytes, false);
        try
        {
            for (int i = 0; i < sections.Count; i++)
            {
                OfficeSlideCanvasModel Canvas() => (extension == ".pptx" ? ReadPptxObjects(package,i) : ReadOdpObjects(package,i)) ?? throw Invalid();
                var section = sections[i]; var canvas = Canvas();
                var title = canvas.Objects[0]; var body = canvas.Objects[1];
                var byId = new Dictionary<string,OfficeSlideObject> { ["Title"] = title, ["Body"] = body };
                if (section.HideTitle) title.Delete!(); else if (section.TitleAngle != 0) title.Rotate!(section.TitleAngle);
                if (section.HideBody) body.Delete!(); else if (section.BodyAngle != 0) body.Rotate!(section.BodyAngle);
                foreach (var extra in section.Shapes)
                {
                    canvas.Insert!(extra.Text, extra.Picture);
                    var obj = Canvas().Objects[^1];
                    byId[extra.Id] = obj;
                    var b = extra.Box;
                    obj.SetBounds(new(b.X * canvas.Width / OfficeDraftSlideCanvas.Width, b.Y * canvas.Height / OfficeDraftSlideCanvas.Height,
                        b.Width * canvas.Width / OfficeDraftSlideCanvas.Width, b.Height * canvas.Height / OfficeDraftSlideCanvas.Height));
                    if (extra.Angle != 0) obj.Rotate!(extra.Angle);
                }
                if (section.ObjectOrder.Count != 0)
                    foreach (var item in OfficeDraftSlideCanvas.Open(section, "", "").Objects)
                        byId[item.Id].ChangeOrder!(true);
            }
            return package.Snapshot()();
        }
        finally { package.EndRead(); }
    }

    private static InvalidDataException Invalid() => new("OfficeEditor.InvalidPackage");
    private static string Normalize(string value) => XmlConvert.VerifyXmlChars(value).ReplaceLineEndings("\n");

    // The common text differ redistributes only a changed substring. The format
    // adapter encodes native breaks/spaces for each affected run separately.
    private sealed class SlideText<T>(List<T> parts, Func<T, string> read, Func<T, string, IEnumerable<T>> replace)
    {
        internal List<T> Parts { get; } = parts;
        internal string Text => string.Concat(Parts.Select(read));
        internal void Write(string value)
        {
            var old = Parts.Select(read).ToArray();
            var projected = old.Select(s => new XText(s)).ToArray();
            OfficeNativeText.Replace(projected, Normalize(value));
            var updated = new List<T>();
            for (int i = 0; i < Parts.Count; i++)
            {
                if (old[i] == projected[i].Value) updated.Add(Parts[i]);
                else updated.AddRange(replace(Parts[i], projected[i].Value));
            }
            Parts.Clear();
            Parts.AddRange(updated);
        }
    }

    private static double FontPoints(double value)
    {
        if (!double.IsFinite(value) || value is < 1 or > 400) throw new ArgumentOutOfRangeException(nameof(value));
        return Math.Round(value, 2, MidpointRounding.AwayFromZero);
    }

    private static double? CommonSize(IEnumerable<double?> values)
    {
        var sizes = values.Distinct().Take(2).ToArray();
        return sizes.Length == 1 ? sizes[0] : null;
    }

    private static string PptxMain(OfficeNativePackage package)
    {
        var main = package.Xml("_rels/.rels").Root?.Elements(Rel + "Relationship")
            .Where(r => ((string?)r.Attribute("Type"))?.EndsWith("/officeDocument", StringComparison.Ordinal) == true).Take(2).ToArray();
        return main is { Length: 1 } ? Target("", main[0]) : throw Invalid();
    }

    private static string Target(string owner, XElement? relationship)
    {
        if (relationship?.Attribute("Target") is not { } target || (string?)relationship.Attribute("TargetMode") == "External") throw Invalid();
        return OfficeNativePackage.Resolve(owner, target.Value);
    }

    private static string RelationsPath(string part)
    {
        int slash = part.LastIndexOf('/') + 1;
        return part[..slash] + "_rels/" + part[slash..] + ".rels";
    }

    internal static bool IsProtected(OfficeNativePackage package, string extension)
        => extension == ".pptx" && package.Xml(PptxMain(package)).Root!.Elements().Any(e => e.Name.LocalName == "modifyVerifier");

    private static IEnumerable<XElement> PptxSlides(OfficeNativePackage package)
    {
        var main = PptxMain(package);
        var root = package.Xml(main).Root ?? throw Invalid();
        var p = root.Name.Namespace;
        if (root.Name.LocalName != "presentation" || (p != P && p != "http://purl.oclc.org/ooxml/presentationml/main")) throw Invalid();
        var rels = package.Xml(RelationsPath(main)).Root!.Elements(Rel + "Relationship")
            .Where(r => ((string?)r.Attribute("Type"))?.EndsWith("/slide", StringComparison.Ordinal) == true)
            .ToDictionary(r => (string)r.Attribute("Id")!, StringComparer.Ordinal);
        foreach (var entry in root.Element(p + "sldIdLst")?.Elements(p + "sldId") ?? [])
        {
            var key = entry.Attributes().FirstOrDefault(attr => attr.Name.LocalName == "id" && attr.Name.Namespace != XNamespace.None)?.Value;
            if (key == null || !rels.TryGetValue(key, out var link)) throw Invalid();
            var slide = package.Xml(Target(main, link)).Root;
            if (slide?.Name != p + "sld") throw Invalid();
            yield return slide;
        }
    }

    // Impress normally skips hidden slides when exporting PDF. Reveal them in
    // a private snapshot so its page indices match the editor's native order.
    internal static byte[] ForEditorPreview(byte[] source, string extension)
    {
        var package = new OfficeNativePackage(source, false);
        try
        {
            if (extension == ".pptx")
            {
                foreach (var slide in PptxSlides(package))
                    if ((string?)slide.Attribute("show") is "0" or "false" or "off") slide.Attribute("show")!.Remove();
            }
            else if (extension == ".odp")
            {
                // ODF stores slide visibility on the drawing-page style. Shared or
                // inherited styles can live in either content.xml or styles.xml.
                foreach (var entry in package.Entries.Where(e => e is "content.xml" or "styles.xml"))
                {
                    foreach (var element in package.Xml(entry).Descendants()
                                 .Where(e => e.Name == Style + "drawing-page-properties" || e.Name == Draw + "page"))
                    {
                        if ((string?)element.Attribute(Pres + "visibility") == "hidden")
                            element.SetAttributeValue(Pres + "visibility", "visible");
                    }
                }
            }
            return package.Snapshot()();
        }
        finally { package.EndRead(); }
    }

    private static IReadOnlyList<OfficeNativePage> ReadPptx(OfficeNativePackage package)
    {
        var pages = new List<OfficeNativePage>();
        foreach (var slide in PptxSlides(package))
        {
            var p = slide.Name.Namespace;
            XNamespace a = p == P ? A : "http://purl.oclc.org/ooxml/drawingml/main";
            var tree = slide.Element(p + "cSld")?.Element(p + "spTree") ?? throw Invalid();
            var blocks = new List<OfficeNativeBlock>();
            OfficeNativeBlock? heading = null;
            foreach (var body in tree.Descendants().Where(e => e.Name == p + "txBody" || e.Name == a + "txBody"))
            {
                if (body.Ancestors().Any(e => e.Name.LocalName == "AlternateContent")) continue;
                var properties = body.Parent?.Element(p + "nvSpPr");
                var placeholder = properties?.Element(p + "nvPr")?.Element(p + "ph");
                if (properties != null && placeholder == null && (bool?)properties.Element(p+"cNvSpPr")?.Attribute("txBox") != true
                    && !body.Descendants(a + "t").Any(t => t.Value.Length > 0)) continue;
                var isTitle = (string?)placeholder?.Attribute("type") is "title" or "ctrTitle";
                foreach (var paragraph in body.Elements(a + "p"))
                {
                    foreach (var block in PptxParagraph(paragraph, a))
                    {
                        if (isTitle) heading ??= block;
                        blocks.Add(block);
                    }
                }
            }
            pages.Add(new(() => heading?.Text ?? blocks.FirstOrDefault(b => !string.IsNullOrWhiteSpace(b.Text))?.Text ?? "", blocks));
        }
        return pages;
    }

    private static IEnumerable<OfficeNativeBlock> PptxParagraph(XElement paragraph, XNamespace a)
    {
        var group = new List<XElement>();
        foreach (var child in paragraph.Elements())
        {
            if (child.Name == a + "r" || child.Name == a + "br") group.Add(child);
            else if (group.Count > 0)
            {
                yield return PptxBlock(group, paragraph, a);
                group = [];
            }
        }
        if (group.Count > 0) yield return PptxBlock(group, paragraph, a);
        else if (!paragraph.Elements().Any(e => e.Name != a + "pPr" && e.Name != a + "endParaRPr"))
        {
            // Represent an empty paragraph without touching XML on open.
            var end = paragraph.Element(a + "endParaRPr");
            var anchor = new XElement(a + "r", end == null ? null : new XElement(a + "rPr", end.Attributes(), end.Nodes()), new XElement(a + "t", ""));
            yield return PptxBlock([anchor], paragraph, a);
        }
    }

    private static OfficeNativeBlock PptxBlock(List<XElement> runs, XElement paragraph, XNamespace a)
    {
        void Attach(XElement run)
        {
            if (run.Parent != null) return;
            var end = paragraph.Element(a + "endParaRPr");
            if (end == null) paragraph.Add(run); else end.AddBeforeSelf(run);
        }
        IEnumerable<XElement> ReplaceRun(XElement run, string value)
        {
            Attach(run);
            var result = new List<XElement>();
            var parts = value.Split('\n');
            for (int i = 0; i < parts.Length; i++)
            {
                var props = run.Element(a + "rPr");
                if (i > 0) result.Add(new XElement(a + "br", props == null ? null : new XElement(props)));
                var next = run.Name == a + "r" ? new XElement(run) : new XElement(a + "r", props == null ? null : new XElement(props));
                if (next.Element(a + "t") is { } t) t.Value = parts[i]; else next.Add(new XElement(a + "t", parts[i]));
                result.Add(next);
            }
            run.ReplaceWith(result);
            return result;
        }
        var text = new SlideText<XElement>(runs, r => r.Name == a + "br" ? "\n" : r.Element(a + "t")?.Value ?? "", ReplaceRun);
        string? ReadAttr(XElement run, string name)
        {
            var level = (int?)paragraph.Element(a + "pPr")?.Attribute("lvl") ?? 0;
            return (string?)run.Element(a + "rPr")?.Attribute(name)
                ?? (string?)paragraph.Element(a + "pPr")?.Element(a + "defRPr")?.Attribute(name)
                ?? (string?)paragraph.Parent?.Element(a + "lstStyle")?.Element(a + $"lvl{level + 1}pPr")?.Element(a + "defRPr")?.Attribute(name);
        }
        bool Has(string key) => text.Parts.All(r => ReadAttr(r, key) is "1" or "true");
        void Set(string key, string val)
        {
            foreach (var run in text.Parts)
            {
                Attach(run);
                var props = run.Element(a + "rPr");
                if (props == null) { props = new XElement(a + "rPr"); run.AddFirst(props); }
                props.SetAttributeValue(key, val);
            }
        }
        return new(() => text.Text, text.Write,
            () => Has("b"), value => Set("b", value ? "1" : "0"),
            () => Has("i"), value => Set("i", value ? "1" : "0"),
            () => CommonSize(text.Parts.Select(r => double.TryParse(ReadAttr(r, "sz"), CultureInfo.InvariantCulture, out var sz) ? (double?)(sz / 100) : null)),
            size => Set("sz", ((int)Math.Round(FontPoints(size) * 100)).ToString(CultureInfo.InvariantCulture)));
    }
}
