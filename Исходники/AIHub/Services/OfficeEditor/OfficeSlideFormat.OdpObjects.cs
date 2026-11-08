using System.Globalization;
using System.Security.Cryptography;
using System.Xml.Linq;

namespace AIHub.Services.OfficeEditor;

internal static partial class OfficeSlideFormat
{
    private static readonly XNamespace OdpManifest = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0";
    private static readonly XNamespace OdpLink = "http://www.w3.org/1999/xlink";

    internal static OfficeSlideCanvasModel? ReadOdpObjects(OfficeNativePackage package, int pageIndex)
    {
        var content = package.Xml("content.xml").Root;
        var page = content?.Element(Office + "body")?.Element(Office + "presentation")?
            .Elements(Draw + "page").ElementAtOrDefault(pageIndex);
        if (page == null || !package.Entries.Contains("styles.xml")) return null;
        var styles = package.Xml("styles.xml").Root;
        var masters = styles?.Element(Office + "master-styles")?.Elements(Style + "master-page");
        var masterName = (string?)page.Attribute(Draw + "master-page-name");
        var master = masters?.FirstOrDefault(m => masterName == null || (string?)m.Attribute(Style + "name") == masterName);
        var layoutName = (string?)master?.Attribute(Style + "page-layout-name");
        var layout = styles?.Element(Office + "automatic-styles")?.Elements(Style + "page-layout")
            .FirstOrDefault(l => layoutName != null && (string?)l.Attribute(Style + "name") == layoutName)
            ?.Element(Style + "page-layout-properties");
        if (OdpLength(layout?.Attribute(Fo + "page-width")) is not double width || width <= 0 ||
            OdpLength(layout?.Attribute(Fo + "page-height")) is not double height || height <= 0) return null;

        var protections = new OdpObjectProtection(content!, styles!);
        var textStyles = new SlideOdfStyles(content!, styles);
        var targets = page.Descendants().Attributes().Where(attr => attr.Name.LocalName is "start-shape" or "end-shape" or "targetElement" ||
            attr.Name == OdpLink + "href" && attr.Value.StartsWith('#')).Select(attr => attr.Value.TrimStart('#')).ToHashSet(StringComparer.Ordinal);
        var objects = new List<OfficeSlideObject>();
        foreach (var shape in OdpShapeOrder(page, protections))
        {
            if (shape.Attribute(Draw + "copy-of") != null || shape.Attribute(Style + "rel-width") != null ||
                shape.Attribute(Style + "rel-height") != null || OdpBounds(shape) is not { } bounds) continue;
            var frame = shape.Name == Draw + "frame";
            var pictures = frame ? shape.Elements(Draw + "image").ToArray() : [];
            // Embedded charts, tables, formulas and media often include a fallback picture in the same
            // frame. That image is merely an alternate rendering of the primary object.
            var drawings = shape.Elements().Where(e => e.Name.Namespace == Draw || e.Name.NamespaceName ==
                "urn:oasis:names:tc:opendocument:xmlns:table:1.0").ToList();
            var picture = pictures.Length > 0 && drawings.All(e => e.Name == Draw + "image");
            var isText = frame ? drawings.Count == 1 && drawings[0].Name == Draw + "text-box"
                : shape.Elements().Any(e => (e.Name == OText + "p" || e.Name == OText + "h" || e.Name == OText + "list") &&
                    !string.IsNullOrWhiteSpace(e.Value));
            if (!picture && !isText) continue;

            var name = (string?)shape.Attribute(Draw + "name") ?? (string?)shape.Element(Svg + "title") ?? "";
            if (name.Length == 0 && isText)
                name = shape.Descendants(OText + "p").Select(p => p.Value.Trim()).FirstOrDefault(s => s.Length != 0) ?? "";
            var id = "odp-" + objects.Count.ToString(CultureInfo.InvariantCulture);
            var canReplace = picture && !shape.AncestorsAndSelf().Any(e => e.Attribute(XNamespace.Xml + "base") != null) &&
                !protections.ContentLocked(shape) && package.Entries.Contains("META-INF/manifest.xml");
            OfficeSlideBounds CurrentBounds() => OdpBounds(shape) ?? bounds;
            double Angle() { OdpPose(shape, out var q, out _, out _); return q; }
            objects.Add(new OfficeSlideObject(id, name, picture, CurrentBounds,
                value => { if (Angle() == 0) WriteOdpBounds(shape, CurrentBounds(), value); else SetOdpPose(shape, value, Angle()); },
                canReplace ? image => ReplaceOdpPicture(package, shape, image) : null)
            { Identity = shape, ChangeOrder = front => OrderOdpShape(shape, front),
              TextBlocks = picture || protections.ContentLocked(shape) ? null : () => shape.Descendants()
                  .Where(e => (e.Name == OText+"p" || e.Name == OText+"h") &&
                      !e.Ancestors().TakeWhile(a => a != shape).Any(a => a.Name == OText+"note" || a.Name == Office+"annotation"))
                  .SelectMany(par => OdpParagraph(par,textStyles)).ToArray(),
              Angle = Angle, Rotate = delta => SetOdpPose(shape, CurrentBounds(), Angle()+delta),
              Delete = protections.ContentLocked(shape) || shape.DescendantsAndSelf().Attributes()
                .Any(a => a.Name.LocalName == "id" && targets.Contains(a.Value)) ? null : shape.Remove });
        }
        return new OfficeSlideCanvasModel(width, height, objects) { Insert = (text, picture) =>
            InsertOdpObject(package, page, width, height, text, picture) };
    }

    private static double? OdpLength(XAttribute? attribute)
    {
        var value = attribute?.Value.Trim();
        if (value == "0") return 0;
        var points = OdfPoints(value);
        if (points == null && value?.EndsWith("px", StringComparison.Ordinal) == true &&
            double.TryParse(value[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var px))
            points = px * 72 / 96;
        return points is double n && double.IsFinite(n) ? n : null;
    }

    private static OfficeSlideBounds? OdpBounds(XElement shape)
    {
        var x = shape.Attribute(Svg + "x");
        var y = shape.Attribute(Svg + "y");
        if (OdpLength(shape.Attribute(Svg + "width")) is not double width || width <= 0 ||
            OdpLength(shape.Attribute(Svg + "height")) is not double height || height <= 0) return null;
        var left = x == null ? 0 : OdpLength(x);
        var top = y == null ? 0 : OdpLength(y);
        if (shape.Attribute(Draw + "transform") != null)
        {
            if (!OdpPose(shape, out var turns, out var tx, out var ty)) return null;
            var half = OdpHalfVector(turns, width, height);
            left = tx + half.X - width/2; top = ty + half.Y - height/2;
        }
        return left != null && top != null ? new OfficeSlideBounds(left.Value, top.Value, width, height) : null;
    }

    private static void WriteOdpBounds(XElement shape, OfficeSlideBounds previous, OfficeSlideBounds next)
    {
        static string Measure(double value) => value.ToString("0.########", CultureInfo.InvariantCulture) + "pt";
        if (previous.X != next.X) shape.SetAttributeValue(Svg + "x", Measure(next.X));
        if (previous.Y != next.Y) shape.SetAttributeValue(Svg + "y", Measure(next.Y));
        if (previous.Width != next.Width) shape.SetAttributeValue(Svg + "width", Measure(next.Width));
        if (previous.Height != next.Height) shape.SetAttributeValue(Svg + "height", Measure(next.Height));
        if (previous != next && shape.Attribute(Pres + "class") != null && shape.Name == Draw + "frame")
            shape.SetAttributeValue(Pres + "user-transformed", "true");
    }

    private static IEnumerable<XElement> OdpShapeOrder(XElement parent, OdpObjectProtection protections, int depth = 0)
    {
        if (depth > 128) yield break;
        var children = parent.Elements().Where(e => e.Name.Namespace == Draw);
        foreach (var item in children.Select((shape, order) => (shape, order))
                     .OrderBy(pair => int.TryParse((string?)pair.shape.Attribute(Draw + "z-index"),
                         NumberStyles.None, CultureInfo.InvariantCulture, out var z) ? z : pair.order))
        {
            var shape = item.shape;
            if (protections.GeometryLocked(shape) || shape.Attribute(Draw + "transform") != null &&
                (shape.Name == Draw + "g" || shape.Name == Draw + "a" || !OdpPose(shape, out _, out _, out _))) continue;
            if (shape.Name == Draw + "g" || shape.Name == Draw + "a")
            {
                foreach (var child in OdpShapeOrder(shape, protections, depth + 1)) yield return child;
            }
            else yield return shape;
        }
    }

    private static void ReplaceOdpPicture(OfficeNativePackage package, XElement frame, OfficeSlidePicture picture)
    {
        var manifest = package.Xml("META-INF/manifest.xml").Root;
        if (manifest?.Name != OdpManifest + "manifest") throw Invalid();
        var images = frame.Elements(Draw + "image").ToArray();
        var primary = images[0];
        var stem = "Pictures/lopata-" + Convert.ToHexString(SHA256.HashData(picture.Bytes)).ToLowerInvariant();
        var entry = stem + picture.Extension;
        var suffix = 0;
        while (true)
        {
            var record = manifest.Elements(OdpManifest + "file-entry").FirstOrDefault(e =>
                (string?)e.Attribute(OdpManifest + "full-path") == entry);
            if (!package.Entries.Contains(entry) && record == null)
            {
                package.AddBinary(entry, picture.Bytes);
                manifest.Add(new XElement(OdpManifest + "file-entry", new XAttribute(OdpManifest + "full-path", entry),
                    new XAttribute(OdpManifest + "media-type", picture.MimeType)));
                break;
            }
            if ((string?)record?.Attribute(OdpManifest + "media-type") == picture.MimeType &&
                package.Entries.Contains(entry) && package.Read(entry).AsSpan().SequenceEqual(picture.Bytes)) break;
            entry = stem + "-" + (++suffix).ToString(CultureInfo.InvariantCulture) + picture.Extension;
        }
        primary.SetAttributeValue(OdpLink + "href", entry);
        primary.SetAttributeValue(OdpLink + "type", "simple");
        primary.SetAttributeValue(OdpLink + "show", "embed");
        primary.SetAttributeValue(OdpLink + "actuate", "onLoad");
        foreach (var attr in primary.Attributes().Where(a => a.Name.LocalName == "mime-type").ToArray())
            attr.Value = picture.MimeType;
        primary.Elements(Office + "binary-data").Remove();
        foreach (var alternate in images.Skip(1)) alternate.Remove();
    }

    private sealed class OdpObjectProtection
    {
        private readonly Dictionary<(string Family, string Name), XElement> _styles = [];
        private readonly HashSet<string> _lockedLayers = new(StringComparer.Ordinal);
        internal OdpObjectProtection(XElement content, XElement styles)
        {
            foreach (var root in new[] { styles, content })
            {
                foreach (var group in root.Elements().Where(e => e.Name == Office + "styles" || e.Name == Office + "automatic-styles"))
                foreach (var style in group.Elements().Where(e => e.Name == Style + "style" || e.Name == Style + "default-style"))
                {
                    var family = (string?)style.Attribute(Style + "family");
                    if (family is "graphic" or "presentation")
                        _styles[(family, (string?)style.Attribute(Style + "name") ?? "")] = style;
                }
            }
            foreach (var layer in styles.Descendants(Draw + "layer"))
                if (((string?)layer.Attribute(Draw + "protected") is "true" or "1" ||
                    (string?)layer.Attribute(Draw + "display") is "none" or "printer") && layer.Attribute(Draw + "name") is { } name)
                    _lockedLayers.Add(name.Value);
        }

        internal bool ContentLocked(XElement shape) => shape.AncestorsAndSelf().Any(e => Protects(e, "content"));
        internal bool GeometryLocked(XElement shape) => _lockedLayers.Contains((string?)shape.Attribute(Draw + "layer") ?? "") ||
            (string?)shape.Attribute(Pres + "placeholder") is "true" or "1" || Protects(shape, "position", "size");

        private bool Protects(XElement shape, params string[] tokens)
        {
            var family = shape.Attribute(Pres + "style-name") == null ? "graphic" : "presentation";
            var name = (string?)shape.Attribute(family == "graphic" ? Draw + "style-name" : Pres + "style-name") ?? "";
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (visited.Add(name) && _styles.TryGetValue((family, name), out var style))
            {
                if (style.Element(Style + "graphic-properties")?.Attribute(Style + "protect") is { } protect)
                    return protect.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Any(tokens.Contains);
                name = (string?)style.Attribute(Style + "parent-style-name") ?? "";
            }
            return false;
        }
    }
}
