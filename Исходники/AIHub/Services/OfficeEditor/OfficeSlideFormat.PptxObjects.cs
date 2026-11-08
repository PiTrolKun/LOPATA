using System.Globalization;
using System.Xml.Linq;

namespace AIHub.Services.OfficeEditor;

internal static partial class OfficeSlideFormat
{
    private readonly record struct PptxSpace(double X, double Y, double ScaleX, double ScaleY)
    {
        internal static PptxSpace Page => new(0, 0, 1, 1);
        internal OfficeSlideBounds ToPage(OfficeSlideBounds b) =>
            new(X + b.X * ScaleX, Y + b.Y * ScaleY, b.Width * ScaleX, b.Height * ScaleY);
        internal OfficeSlideBounds FromPage(OfficeSlideBounds b) =>
            new((b.X - X) / ScaleX, (b.Y - Y) / ScaleY, b.Width / ScaleX, b.Height / ScaleY);
    }

    internal static OfficeSlideCanvasModel? ReadPptxObjects(OfficeNativePackage package, int pageIndex)
    {
        var main = PptxMain(package);
        var presentation = package.Xml(main).Root!;
        var p = presentation.Name.Namespace;
        XNamespace a = p == P ? A : "http://purl.oclc.org/ooxml/drawingml/main";
        XNamespace r = p == P ? R : "http://purl.oclc.org/ooxml/officeDocument/relationships";
        var size = presentation.Element(p + "sldSz");
        if (PptxLength(size, "cx") is not { } width || PptxLength(size, "cy") is not { } height ||
            width <= 0 || height <= 0 || pageIndex < 0) return null;
        var id = (string?)presentation.Element(p + "sldIdLst")?.Elements(p + "sldId").ElementAtOrDefault(pageIndex)?.Attribute(r + "id");
        if (id == null) return null;
        var slidePart = Target(main, package.Xml(RelationsPath(main)).Root!.Elements(Rel + "Relationship")
            .FirstOrDefault(e => (string?)e.Attribute("Id") == id && (string?)e.Attribute("Type") == r.NamespaceName + "/slide"));
        var tree = package.Xml(slidePart).Root?.Element(p + "cSld")?.Element(p + "spTree");
        var objects = new List<OfficeSlideObject>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (tree == null) return null;
        var referencedIds = tree.Document!.Descendants().Select(e => e.Name.LocalName is "stCxn" or "endCxn"
            ? (string?)e.Attribute("id") : (string?)e.Attribute("spid")).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var layout = new Lazy<string?>(() => PptxRelatedPart(package, slidePart, r.NamespaceName + "/slideLayout"));
        var master = new Lazy<string?>(() => layout.Value is { } part ? PptxRelatedPart(package, part, r.NamespaceName + "/slideMaster") : null);

        void Visit(XElement group, PptxSpace space, int depth = 0)
        {
            if (depth > 128) return;
            foreach (var shape in group.Elements())
            {
                if (shape.Name == p + "grpSp")
                {
                    if (!PptxLocked(shape.Element(p + "nvGrpSpPr")) &&
                        PptxGroupSpace(shape.Element(p + "grpSpPr")?.Element(a + "xfrm"), a, space) is { } childSpace)
                        Visit(shape, childSpace, depth + 1);
                    continue;
                }
                bool picture = shape.Name == p + "pic";
                if (!picture && shape.Name != p + "sp") continue;
                var nv = shape.Element(p + (picture ? "nvPicPr" : "nvSpPr"));
                var identity = nv?.Element(p + "cNvPr");
                var shapeId = (string?)identity?.Attribute("id");
                if (shapeId == null || !ids.Add(shapeId) || PptxLocked(nv)) continue;
                var ph = nv?.Element(p + "nvPr")?.Element(p + "ph");
                var blip = shape.Element(p + "blipFill")?.Element(a + "blip");
                if (picture)
                {
                    // Video and audio posters look like pictures in the tree but represent media playback.
                    if (blip == null || nv!.Descendants().Any(e => e.Name.LocalName is "videoFile" or "audioFile" or "media" or "quickTimeFile")) continue;
                }
                else
                {
                    var body = shape.Element(p + "txBody");
                    if (body == null || ph == null && !PptxTrue(nv?.Element(p + "cNvSpPr")?.Attribute("txBox"))
                        && !body.Descendants(a + "t").Any(t => !string.IsNullOrEmpty(t.Value))) continue;
                }
                var spPr = shape.Element(p + "spPr");
                if (spPr == null) continue;
                var local = spPr.Element(a + "xfrm");
                var source = local ?? (ph == null ? null : InheritedPptxTransform(package, layout.Value, master, ph, p, a));
                if (source == null ||
                    (!PptxStraight(source) && space.ScaleX != space.ScaleY) ||
                    PptxBounds(source, a) is not { } initial || !space.ToPage(initial).IsValid) continue;
                OfficeSlideBounds LocalBounds() => PptxBounds(spPr.Element(a + "xfrm"), a) ?? initial;
                OfficeSlideBounds Read() => space.ToPage(LocalBounds());
                void Write(OfficeSlideBounds value)
                {
                    var target = space.FromPage(value);
                    // DrawingML stores integer EMUs. Validate every coordinate before changing the document.
                    static long Emu(double d) => checked((long)Math.Round(d, MidpointRounding.AwayFromZero));
                    var x = Emu(target.X); var y = Emu(target.Y);
                    var cx = Math.Max(1, Emu(target.Width)); var cy = Math.Max(1, Emu(target.Height));
                    var box = LocalBounds();
                    if (x == box.X && y == box.Y && cx == box.Width && cy == box.Height) return;
                    local = spPr.Element(a + "xfrm");
                    if (local == null)
                    {
                        local = new XElement(source!);
                        spPr.AddFirst(local);
                    }
                    var off = local.Element(a + "off")!; var ext = local.Element(a + "ext")!;
                    off.SetAttributeValue("x", x); off.SetAttributeValue("y", y);
                    ext.SetAttributeValue("cx", cx); ext.SetAttributeValue("cy", cy);
                }
                var name = (string?)identity?.Attribute("name") ?? shapeId;
                double Angle() => ((PptxLength(local ?? source, "rot") ?? 0) / 60000d) % 360;
                void Rotate(double delta)
                {
                    if (local == null) { local = new XElement(source); spPr.AddFirst(local); }
                    local.SetAttributeValue("rot", (int)Math.Round(((Angle() + delta) % 360 + 360) % 360 * 60000));
                }
                objects.Add(new(shapeId, name, picture, Read, Write, picture ?
                    image => ReplacePptxPicture(package, main, slidePart, blip!, r, image) : null)
                {
                    Identity = shape,
                    ChangeOrder = front => OrderPptxShape(shape, front),
                    TextBlocks = picture || nv!.Descendants().Any(e => PptxTrue(e.Attribute("noTextEdit"))) ? null :
                        () => shape.Element(p+"txBody")!.Elements(a+"p").SelectMany(par => PptxParagraph(par,a)).ToArray(),
                    Angle = Angle, Rotate = space.ScaleX == space.ScaleY &&
                        !nv!.Descendants().Any(e => PptxTrue(e.Attribute("noRot"))) ? Rotate : null,
                    Delete = referencedIds.Contains(shapeId) ? null : shape.Remove
                });
            }
        }

        // The document-level group often carries the all-zero identity transform.
        var rootTransform = tree.Element(p + "grpSpPr")?.Element(a + "xfrm");
        var identityRoot = rootTransform == null || PptxStraight(rootTransform) && !PptxTrue(rootTransform.Attribute("flipH")) &&
            !PptxTrue(rootTransform.Attribute("flipV")) && rootTransform.Elements().SelectMany(e => e.Attributes()).All(attr => attr.Value == "0");
        var rootSpace = identityRoot ? PptxSpace.Page : PptxGroupSpace(rootTransform, a, PptxSpace.Page);
        if (rootSpace is {} space) Visit(tree, space);
        return new(width, height, objects) { Insert = rootSpace is {} writable ?
            (text, image) => InsertPptxObject(package, main, slidePart, tree, writable.FromPage(InsertionBounds(width,height,image)),
                text, image, p, a, r) : null };
    }

    private static bool PptxTrue(XAttribute? attr) => attr?.Value is "1" or "true" or "on";
    private static bool PptxLocked(XElement? nv) =>
        PptxTrue(nv?.Elements().FirstOrDefault(e => e.Name.LocalName == "cNvPr")?.Attribute("hidden")) ||
        nv?.Descendants().Any(e => e.Name.LocalName.EndsWith("Locks", StringComparison.Ordinal) &&
            (PptxTrue(e.Attribute("noMove")) || PptxTrue(e.Attribute("noResize")) || PptxTrue(e.Attribute("noSelect")))) == true;

    private static string? PptxRelatedPart(OfficeNativePackage package, string owner, string type)
    {
        var path = RelationsPath(owner);
        if (!package.Entries.Contains(path, StringComparer.Ordinal)) return null;
        var rel = package.Xml(path).Root?.Elements(Rel + "Relationship").FirstOrDefault(e =>
            (string?)e.Attribute("Type") == type && (string?)e.Attribute("TargetMode") != "External");
        if (rel == null) return null;
        var part = Target(owner, rel);
        return package.Entries.Contains(part, StringComparer.Ordinal) ? part : null;
    }

    private static XElement? InheritedPptxTransform(OfficeNativePackage package, string? layoutPart,
        Lazy<string?> master, XElement placeholder, XNamespace p, XNamespace a)
    {
        IEnumerable<(XElement Shape, XElement Ph)> Placeholders(string part)
        {
            foreach (var shape in package.Xml(part).Root?.Element(p + "cSld")?.Element(p + "spTree")?.Elements(p + "sp") ?? [])
                if (shape.Element(p + "nvSpPr")?.Element(p + "nvPr")?.Element(p + "ph") is { } ph)
                    yield return (shape, ph);
        }
        if (layoutPart == null) return null;
        var index = (string?)placeholder.Attribute("idx") ?? "0";
        var layout = Placeholders(layoutPart).FirstOrDefault(o => ((string?)o.Ph.Attribute("idx") ?? "0") == index);
        if (layout.Shape == null) return null;
        if (layout.Shape.Element(p + "spPr")?.Element(a + "xfrm") is { } transform) return transform;
        if (master.Value == null) return null;
        var type = (string?)layout.Ph.Attribute("type") ?? "obj";
        var masterType = type switch
        {
            "ctrTitle" => "title",
            "subTitle" or "obj" or "chart" or "tbl" or "clipArt" or "dgm" or "media" or "pic" => "body",
            _ => type
        };
        return Placeholders(master.Value).FirstOrDefault(o => (string?)o.Ph.Attribute("type") == masterType)
            .Shape?.Element(p + "spPr")?.Element(a + "xfrm");
    }

    private static double? PptxLength(XElement? element, string attr) =>
        long.TryParse((string?)element?.Attribute(attr), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static bool PptxStraight(XElement? transform) => transform != null &&
        (transform.Attribute("rot") == null || PptxLength(transform, "rot") % 21_600_000 == 0);

    private static OfficeSlideBounds? PptxBounds(XElement? xf, XNamespace a, bool child = false)
    {
        var off = xf?.Element(a + (child ? "chOff" : "off")); var ext = xf?.Element(a + (child ? "chExt" : "ext"));
        if (PptxLength(off, "x") is { } x && PptxLength(off, "y") is { } y &&
            PptxLength(ext, "cx") is { } width && PptxLength(ext, "cy") is { } height && width > 0 && height > 0)
            return new(x, y, width, height);
        return null;
    }

    private static PptxSpace? PptxGroupSpace(XElement? xf, XNamespace a, PptxSpace parent)
    {
        if (xf == null) return parent;
        if (!PptxStraight(xf) || PptxTrue(xf.Attribute("flipH")) || PptxTrue(xf.Attribute("flipV")) ||
            PptxBounds(xf, a) is not { } box || PptxBounds(xf, a, child: true) is not { } inner) return null;
        var page = parent.ToPage(box);
        var scaleX = page.Width / inner.Width; var scaleY = page.Height / inner.Height;
        return new(page.X - inner.X * scaleX, page.Y - inner.Y * scaleY, scaleX, scaleY);
    }

    private static void ReplacePptxPicture(OfficeNativePackage package, string main, string slide, XElement blip,
        XNamespace r, OfficeSlidePicture image)
    {
        // A part or a relationship can be shared even between two pictures on the same slide.
        // Point just this blip to a content-addressed part, leaving every old consumer intact.
        var types = package.Xml("[Content_Types].xml").Root ?? throw Invalid();
        var typeNs = types.Name.Namespace;
        var relPath = RelationsPath(slide);
        var rels = package.Entries.Contains(relPath, StringComparer.Ordinal) ? package.Xml(relPath).Root! : null;
        var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(image.Bytes)).ToLowerInvariant();
        var prefix = main[..(main.LastIndexOf('/') + 1)] + "media/lopata-" + fingerprint;
        var path = prefix + image.Extension;
        var contentTypes = types.Elements(typeNs + "Override").ToDictionary(e => (string)e.Attribute("PartName")!, StringComparer.OrdinalIgnoreCase);
        var defaultType = types.Elements(typeNs + "Default").FirstOrDefault(e =>
            string.Equals((string?)e.Attribute("Extension"), image.Extension.TrimStart('.'), StringComparison.OrdinalIgnoreCase));
        var defaultMime = (string?)defaultType?.Attribute("ContentType");
        var index = 0;
        while (package.Entries.Contains(path, StringComparer.OrdinalIgnoreCase) || contentTypes.ContainsKey("/" + path))
        {
            var existingMime = contentTypes.TryGetValue("/" + path, out var existing) ? (string?)existing.Attribute("ContentType") : defaultMime;
            if (existingMime == image.MimeType && package.Entries.Contains(path, StringComparer.Ordinal) &&
                package.Read(path).AsSpan().SequenceEqual(image.Bytes)) break;
            path = prefix + "-" + ++index + image.Extension;
        }
        var target = RelativePart(slide, path);
        var imageType = r.NamespaceName + "/image";
        var link = rels?.Elements(Rel + "Relationship").FirstOrDefault(e =>
            (string?)e.Attribute("Type") == imageType && (string?)e.Attribute("TargetMode") != "External" &&
            (string?)e.Attribute("Target") == target);
        var nextId = (string?)link?.Attribute("Id") ?? NextName("rId", rels?.Elements(Rel + "Relationship").Select(e => (string?)e.Attribute("Id")) ?? []);
        bool addImage = !package.Entries.Contains(path, StringComparer.Ordinal);
        if (package.Entries.Count + (addImage ? 1 : 0) + (rels == null ? 1 : 0) > 16_000) throw Invalid();
        if (addImage) package.AddBinary(path, image.Bytes);
        if (rels == null)
        {
            rels = new XElement(Rel + "Relationships");
            package.AddXml(relPath, new XDocument(rels));
        }
        if (link == null)
            rels.Add(new XElement(Rel + "Relationship", new XAttribute("Id", nextId),
                new XAttribute("Type", imageType), new XAttribute("Target", target)));
        if (!contentTypes.ContainsKey("/" + path) && defaultMime != image.MimeType)
            types.Add(new XElement(typeNs + "Override", new XAttribute("PartName", "/" + path), new XAttribute("ContentType", image.MimeType)));
        blip.SetAttributeValue(r + "embed", nextId);
        blip.Attribute(r + "link")?.Remove();
        // Modern PowerPoint prefers the optional SVG/HD Photo over the fallback raster.
        // Removing those overrides also ensures that replacing an SVG changes what PowerPoint displays.
        foreach (var ext in blip.Elements().Where(e => e.Name.LocalName == "extLst").Elements().Where(e =>
            e.Descendants().Any(d => d.Name.LocalName is "svgBlip" or "imgProps")).ToArray())
            ext.Remove();
    }
}
