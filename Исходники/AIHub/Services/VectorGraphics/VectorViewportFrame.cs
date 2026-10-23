using System.Globalization;
using System.Xml.Linq;

namespace AIHub.Services.VectorGraphics;

/// <summary>Renders only the visible viewport; zoom never allocates a full image at the enlarged canvas size.</summary>
internal static class VectorViewportFrame
{
    internal static string Compose(string svg, double width, double height, double aspect,
        double scale, double offsetX, double offsetY)
    {
        if (new[] { width, height, aspect, scale, offsetX, offsetY }.Any(n => !double.IsFinite(n))
            || width <= 0 || height <= 0 || aspect <= 0 || scale <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        var original = VectorSvg.Parse(svg).Root!;
        var fitWidth = Math.Min(width, height * aspect); var fitHeight = fitWidth / aspect;
        string N(double n) => n.ToString("G17", CultureInfo.InvariantCulture);
        original.SetAttributeValue("width", N(fitWidth)); original.SetAttributeValue("height", N(fitHeight));
        original.SetAttributeValue("x", N((width - fitWidth) / 2)); original.SetAttributeValue("y", N((height - fitHeight) / 2));
        XNamespace ns = "http://www.w3.org/2000/svg";
        return new XElement(ns + "svg", new XAttribute("width", N(width)), new XAttribute("height", N(height)),
            new XAttribute("viewBox", $"0 0 {N(width)} {N(height)}"),
            new XElement(ns + "g", new XAttribute("transform", $"matrix({N(scale)} 0 0 {N(scale)} {N(offsetX)} {N(offsetY)})"), original))
            .ToString(SaveOptions.DisableFormatting);
    }
}
