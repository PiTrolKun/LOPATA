using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;
using AIHub.Services.VectorGraphics;
using Point = System.Windows.Point;

namespace AIHub.Services.VectorEditor;

/// <summary>Geometry conversion for manual plasticity. Each closed subpath remains separate, including holes.</summary>
public static class VectorPathEditing
{
    public static string Prepare(string svg, IReadOnlyCollection<string> selected)
    {
        var doc = VectorSvg.Parse(svg); var targets = doc.Root!.Descendants()
            .Where(e => selected.Contains((string?)e.Attribute("data-lopata-id") ?? "")).ToArray();
        var changed = 0;
        foreach (var element in targets.SelectMany(e => e.DescendantsAndSelf()).Distinct().ToArray())
        {
            if (element.AncestorsAndSelf().Any(e=>(string?)e.Attribute("data-lopata-locked")=="true"))continue;
            if (element.Ancestors().Any(a => a.Name == VectorEditorSvg.Ns + "defs")) continue;
            var geometry = GeometryFor(element);
            if (geometry is null) continue;
            var bounds = geometry.Bounds;
            var tolerance = Math.Max(.025, Math.Max(bounds.Width, bounds.Height) / 1200);
            var flat = geometry.GetFlattenedPathGeometry(tolerance, ToleranceType.Absolute);
            var spacing = Math.Max(.05, Math.Max(bounds.Width,bounds.Height)/64);
            var path = string.Join(" ", flat.Figures.Select(f => Figure(f,spacing)));
            var replacement = new XElement(VectorEditorSvg.Ns + "path",
                element.Attributes().Where(a => !new[] { "d", "x", "y", "width", "height", "rx", "ry", "cx", "cy", "r", "x1", "y1", "x2", "y2", "points" }.Contains(a.Name.LocalName)),
                new XAttribute("d", path), new XAttribute("data-lopata-plastic", "true"));
            element.ReplaceWith(replacement); changed++;
        }
        if (changed == 0) throw new InvalidDataException("Vector.Editor.Error.PlasticSelection");
        return doc.ToString(SaveOptions.DisableFormatting);
    }
    private static string Point(Point point) => FormattableString.Invariant($"{point.X:0.######} {point.Y:0.######}");
    private static string Figure(PathFigure figure,double spacing)
    {
        var text=new System.Text.StringBuilder("M "+Point(figure.StartPoint));var previous=figure.StartPoint;
        var points=figure.Segments.SelectMany(s=>s is PolyLineSegment lines?lines.Points.AsEnumerable():s is LineSegment line?new[]{line.Point}:Array.Empty<Point>()).ToList();
        if(figure.IsClosed)points.Add(figure.StartPoint);
        foreach(var point in points){
            var count=Math.Max(1,(int)Math.Ceiling((point-previous).Length/spacing));
            for(var i=1;i<=count;i++){var next=previous+(point-previous)*(i/(double)count);text.Append(" L ").Append(Point(next));}
            previous=point;
        }
        if(figure.IsClosed)text.Append(" Z");return text.ToString();
    }
    private static Geometry? GeometryFor(XElement e)
    {
        double N(string key, double fallback = 0) => double.TryParse((string?)e.Attribute(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
            ? value : e.Attribute(key) is null ? fallback : throw new InvalidDataException("Vector.Editor.Error.Geometry");
        return e.Name.LocalName switch
        {
            "path" => Geometry.Parse((string?)e.Attribute("d") ?? ""),
            "rect" => new RectangleGeometry(new Rect(N("x"), N("y"), Math.Max(0, N("width")), Math.Max(0, N("height"))), N("rx"), N("ry", N("rx"))),
            "circle" => new EllipseGeometry(new Point(N("cx"), N("cy")), N("r"), N("r")),
            "ellipse" => new EllipseGeometry(new Point(N("cx"), N("cy")), N("rx"), N("ry")),
            "line" => new LineGeometry(new Point(N("x1"), N("y1")), new Point(N("x2"), N("y2"))),
            "polygon" or "polyline" => Geometry.Parse("M " + (string?)e.Attribute("points") + (e.Name.LocalName == "polygon" ? " Z" : "")),
            _ => null
        };
    }
}
