using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SkiaSharp;

namespace AIHub.Services.OfficeEditor;

internal static partial class OfficeSlideFormat
{
    internal static OfficeSlideBounds InsertionBounds(double width, double height, OfficeSlidePicture? picture)
    {
        var w = width * .45;
        var h = height * .18;
        if (picture != null)
        {
            using var codec = SKCodec.Create(new SKMemoryStream(picture.Bytes)) ?? throw Invalid();
            var scale = Math.Min(w / codec.Info.Width, height * .55 / codec.Info.Height);
            w = codec.Info.Width * scale;
            h = codec.Info.Height * scale;
        }
        return new((width - w) / 2, (height - h) / 2, w, h);
    }

    private static void InsertPptxObject(OfficeNativePackage package, string main, string slide, XElement tree,
        OfficeSlideBounds b, string text, OfficeSlidePicture? picture, XNamespace p, XNamespace a, XNamespace r)
    {
        var ids = tree.Descendants(p + "cNvPr").Select(e => (uint?)e.Attribute("id")).ToHashSet();
        var id = 2;
        while (ids.Contains((uint)id)) id++;
        var shape = TextBox(id, "Text " + id, b.Y, b.Height, 2400, false, text, b.X, b.Width);
        if (picture != null)
        {
            var blip = new XElement(A + "blip");
            var properties = shape.Element(P + "spPr")!;
            properties.Element(A + "noFill")?.Remove();
            shape = new XElement(P + "pic", new XElement(P + "nvPicPr",
                new XElement(P + "cNvPr", new XAttribute("id", id), new XAttribute("name", "Picture " + id)),
                new XElement(P + "cNvPicPr"), new XElement(P + "nvPr")),
                new XElement(P + "blipFill", blip, new XElement(A + "stretch", new XElement(A + "fillRect"))), properties);
            ReplacePptxPicture(package, main, slide, blip, r, picture);
        }
        if (p != P)
            foreach (var node in shape.DescendantsAndSelf())
                node.Name = (node.Name.Namespace == P ? p : a) + node.Name.LocalName;
        if (tree.Element(p + "extLst") is {} tail) tail.AddBeforeSelf(shape);
        else tree.Add(shape);
    }

    private static string Pt(double value) => value.ToString("0.##########", CultureInfo.InvariantCulture) + "pt";
    private static void InsertOdpObject(OfficeNativePackage package, XElement page, double width, double height,
        string text, OfficeSlidePicture? picture)
    {
        var b = InsertionBounds(width, height, picture);
        var frame = new XElement(Draw + "frame", new XAttribute(Draw + "name", picture == null ? text : "Picture"),
            new XAttribute(Svg + "x", Pt(b.X)), new XAttribute(Svg + "y", Pt(b.Y)),
            new XAttribute(Svg + "width", Pt(b.Width)), new XAttribute(Svg + "height", Pt(b.Height)));
        if (picture == null)
        {
            frame.Add(new XElement(Draw + "text-box", Normalize(text).Split('\n')
                .Select(line => new XElement(OText + "p", OdfTextNodes(line)))));
            var content = page.Document!.Root!;
            var styles = content.Element(Office + "automatic-styles");
            if (styles == null) { styles = new XElement(Office + "automatic-styles"); content.Element(Office + "body")!.AddBeforeSelf(styles); }
            var existing = styles.Elements(Style + "style").FirstOrDefault(e =>
                ((string?)e.Attribute(Style + "name"))?.StartsWith("LopataTextFrame", StringComparison.Ordinal) == true &&
                (string?)e.Attribute(Style + "family") == "graphic" &&
                (string?)e.Element(Style + "graphic-properties")?.Attribute(Draw + "fill") == "none" &&
                (string?)e.Element(Style + "graphic-properties")?.Attribute(Draw + "stroke") == "none");
            if (existing == null)
            {
                var names = styles.Elements().Concat(package.Xml("styles.xml").Descendants(Style+"style"))
                    .Select(e => (string?)e.Attribute(Style + "name"));
                existing = new XElement(Style + "style", new XAttribute(Style + "name", NextName("LopataTextFrame", names)),
                    new XAttribute(Style + "family", "graphic"), new XElement(Style + "graphic-properties",
                    new XAttribute(Draw + "fill", "none"), new XAttribute(Draw + "stroke", "none")));
                styles.Add(existing);
            }
            frame.SetAttributeValue(Draw + "style-name", existing.Attribute(Style + "name")!.Value);
        }
        else { frame.Add(new XElement(Draw + "image")); ReplaceOdpPicture(package, frame, picture); }
        if (page.Elements().Any(e => e.Attribute(Draw + "z-index") != null))
        {
            var z = page.Elements().Select((e,index) => int.TryParse((string?)e.Attribute(Draw + "z-index"), out var i) ? i : index).Max();
            frame.SetAttributeValue(Draw + "z-index", (long)z + 1);
        }
        var tail = page.Elements().FirstOrDefault(e => e.Name == Pres + "notes" || e.Name.NamespaceName.Contains("animation"));
        if (tail == null) page.Add(frame); else tail.AddBeforeSelf(frame);
    }

    private static bool OdpPose(XElement shape, out double degrees, out double x, out double y)
    {
        degrees = 0; x = y = 0;
        if (shape.Attribute(Draw + "transform") is not {} transform) return true;
        var match = Regex.Match(transform.Value, @"^\s*rotate\s*\(\s*([-+.0-9Ee]+)\s*\)\s*translate\s*\(\s*([^,\s]+)[,\s]+([^\s)]+)\s*\)\s*$");
        if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var angle)
            || !double.IsFinite(angle) || OdpLength(new XAttribute("x", match.Groups[2].Value)) is not double tx
            || OdpLength(new XAttribute("y", match.Groups[3].Value)) is not double ty) return false;
        degrees = ((-angle * 180 / Math.PI) % 360 + 360) % 360;
        x = tx; y = ty; return true;
    }

    private static (double X, double Y) OdpHalfVector(double angle, double w, double h)
    {
        var r = angle * Math.PI / 180;
        return ((w*Math.Cos(r) - h*Math.Sin(r))/2, (w*Math.Sin(r) + h*Math.Cos(r))/2);
    }

    private static void SetOdpPose(XElement shape, OfficeSlideBounds b, double degrees)
    {
        var q = (degrees % 360 + 360) % 360;
        shape.SetAttributeValue(Svg + "width", Pt(b.Width)); shape.SetAttributeValue(Svg + "height", Pt(b.Height));
        if (q == 0)
        {
            shape.Attribute(Draw + "transform")?.Remove();
            shape.SetAttributeValue(Svg + "x", Pt(b.X)); shape.SetAttributeValue(Svg + "y", Pt(b.Y));
        }
        else
        {
            var offset = OdpHalfVector(q, b.Width, b.Height);
            shape.Attribute(Svg + "x")?.Remove(); shape.Attribute(Svg + "y")?.Remove();
            shape.SetAttributeValue(Draw + "transform", FormattableString.Invariant(
                $"rotate({-q * Math.PI / 180:R}) translate({Pt(b.X+b.Width/2-offset.X)} {Pt(b.Y+b.Height/2-offset.Y)})"));
        }
        if (shape.Attribute(Pres + "class") != null) shape.SetAttributeValue(Pres + "user-transformed", "true");
    }
}
