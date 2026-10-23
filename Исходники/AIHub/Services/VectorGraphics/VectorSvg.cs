using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace AIHub.Services.VectorGraphics;

public sealed record PreparedVectorSvg(string Svg, int SyntaxCorrections);

/// <summary>Static self-contained SVG only. Content validation is independent of component audit mode.</summary>
public static partial class VectorSvg
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
    private static readonly HashSet<string> Elements = new(("svg g defs path rect circle ellipse line polyline polygon text tspan textPath linearGradient radialGradient stop clipPath mask filter feGaussianBlur feMerge feMergeNode feOffset feFlood feComposite feBlend feColorMatrix feComponentTransfer feFuncR feFuncG feFuncB feFuncA title desc metadata").Split(' '), StringComparer.Ordinal);
    [GeneratedRegex("url\\(\\s*['\"]?#[A-Za-z0-9_.:-]+['\"]?\\s*\\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LocalUrl();
    [GeneratedRegex(@"\burl\s*\(|@|expression\s*\(|javascript:|https?:|file:|data:|\\|/\*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnsafeValue();
    [GeneratedRegex(@"(<tspan\b[^<>]*>[^<>]*)</span\s*>", RegexOptions.CultureInvariant)]
    private static partial Regex MistypedTspanEnd();

    public static PreparedVectorSvg Prepare(string answer)
    {
        try { return new(Extract(answer), 0); }
        catch (InvalidDataException error) when (error.InnerException is XmlException)
        {
            var start = answer.IndexOf("<svg", StringComparison.OrdinalIgnoreCase);
            var end = answer.LastIndexOf("</svg>", StringComparison.OrdinalIgnoreCase);
            if (start < 0 || end < start) throw;
            var candidate = answer[start..(end + 6)]; var count = 0;
            candidate = MistypedTspanEnd().Replace(candidate, match => { count++; return match.Groups[1].Value + "</tspan>"; });
            if (count == 0) throw;
            _ = Parse(candidate); // The full content policy still applies after this narrow syntax correction.
            return new(candidate, count);
        }
    }

    public static string Extract(string answer)
    {
        var start = answer.IndexOf("<svg", StringComparison.OrdinalIgnoreCase);
        var end = answer.LastIndexOf("</svg>", StringComparison.OrdinalIgnoreCase);
        if (start < 0 || end < start) throw new InvalidDataException("Vector.Error.IncompleteSvg");
        var svg = answer[start..(end + 6)];
        _ = Parse(svg); return svg;
    }
    public static XDocument Parse(string text)
    {
        using var input = new StringReader(text);
        using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null, MaxCharactersInDocument = 32L * 1024 * 1024 });
        XDocument document;
        try { document = XDocument.Load(reader, LoadOptions.PreserveWhitespace); }
        catch (XmlException error) { throw new InvalidDataException("Vector.Error.InvalidSvg", error); }
        if (document.Root?.Name != Svg + "svg" || document.DescendantNodes().OfType<XProcessingInstruction>().Any())
            throw new InvalidDataException("Vector.Error.InvalidSvg");
        var count = 0; var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in document.Root.DescendantsAndSelf())
        {
            if (++count > 100000 || element.Ancestors().Take(129).Count() > 128)
                throw new InvalidDataException("Vector.Error.Complexity");
            if (element.Name.Namespace != Svg || !Elements.Contains(element.Name.LocalName))
                throw new InvalidDataException("Vector.Error.UnsafeSvg");
            foreach (var attribute in element.Attributes())
            {
                if (attribute.IsNamespaceDeclaration) continue;
                var name = attribute.Name.LocalName; var value = attribute.Value;
                if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase) || name is "base" or "src"
                    || attribute.Name.Namespace != XNamespace.None && attribute.Name.Namespace != XNamespace.Xml
                        && attribute.Name.NamespaceName != "http://www.w3.org/1999/xlink")
                    throw new InvalidDataException("Vector.Error.UnsafeSvg");
                if (name == "id" && !ids.Add(value)) throw new InvalidDataException("Vector.Error.InvalidSvg");
                if (name == "href" && (!value.StartsWith('#') || value.Length < 2))
                    throw new InvalidDataException("Vector.Error.UnsafeSvg");
                if (UnsafeValue().IsMatch(LocalUrl().Replace(value, "")))
                    throw new InvalidDataException("Vector.Error.UnsafeSvg");
            }
        }
        var box = document.Root.Attribute("viewBox")?.Value.Split([' ', ',', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (box is not { Length: 4 } || box.Any(n => !double.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || !double.IsFinite(v) || Math.Abs(v) > 1e9)
            || double.Parse(box[2], CultureInfo.InvariantCulture) <= 0 || double.Parse(box[3], CultureInfo.InvariantCulture) <= 0)
            throw new InvalidDataException("Vector.Error.ViewBox");
        return document;
    }
    public static string WithProvenance(string svg, OutputProvenance provenance, string? author)
    {
        var document = Parse(svg);
        document.Root!.Elements(Svg + "metadata").Where(e => (string?)e.Attribute("id") == "lopata-provenance").Remove();
        document.Root.AddFirst(new XElement(Svg + "metadata", new XAttribute("id", "lopata-provenance"), provenance.Json()));
        if (!string.IsNullOrWhiteSpace(author)) document.Root.AddFirst(new XElement(Svg + "desc", "Author: " + author));
        return document.ToString(SaveOptions.DisableFormatting);
    }
}
