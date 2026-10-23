using System.Globalization;
using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace AIHub.Services.VectorGraphics;

internal sealed record VectorDraftFrame(string Svg, double Aspect);

/// <summary>Reads complete XML tokens from a prefix and temporarily closes open elements. Never changes the saved answer.</summary>
internal static class VectorDraftSvg
{
    private static readonly HashSet<string> Shapes = new("path rect circle ellipse line polyline polygon text".Split(' '));
    private static readonly HashSet<string> Definitions = new("defs clipPath mask filter linearGradient radialGradient".Split(' '));
    internal static VectorDraftFrame? TryCreate(string answer, CancellationToken token = default)
    {
        var start = answer.IndexOf("<svg", StringComparison.Ordinal);
        if (start < 0) return null;
        var document = new XDocument(); var stack = new Stack<XElement>(); var count = 0;
        try
        {
            using var input = new StringReader(answer[start..]);
            using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null, MaxCharactersInDocument = 32L * 1024 * 1024 });
            try
            {
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    if (reader.NodeType == XmlNodeType.Element)
                    {
                        if (++count > 100000 || stack.Count > 128) return null;
                        var element = new XElement(XName.Get(reader.LocalName, reader.NamespaceURI));
                        if (reader.MoveToFirstAttribute())
                        {
                            do { element.Add(new XAttribute(reader.Name == "xmlns" ? XName.Get("xmlns")
                                : XName.Get(reader.LocalName, reader.NamespaceURI), reader.Value)); }
                            while (reader.MoveToNextAttribute());
                            reader.MoveToElement();
                        }
                        if (stack.TryPeek(out var parent)) parent.Add(element);
                        else if (document.Root is null) document.Add(element);
                        else return null;
                        if (!reader.IsEmptyElement) stack.Push(element);
                    }
                    else if (reader.NodeType == XmlNodeType.EndElement) { if (stack.Count > 0) stack.Pop(); }
                    else if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace)
                    { if (stack.TryPeek(out var parent)) parent.Add(new XText(reader.Value)); }
                    else if (reader.NodeType is XmlNodeType.ProcessingInstruction or XmlNodeType.DocumentType) return null;
                }
            }
            catch (XmlException) { /* Expected for an unfinished token or missing closing tags. Validate the constructed tree below. */ }
            if (document.Root is null || !document.Root.Descendants().Any(e => Shapes.Contains(e.Name.LocalName)
                && !e.Ancestors().Any(a => Definitions.Contains(a.Name.LocalName)))) return null;
            var svg = document.ToString(SaveOptions.DisableFormatting);
            var valid = VectorSvg.Parse(svg); // Same content/security/resource policy as a finished SVG, in every audit mode.
            var box = valid.Root!.Attribute("viewBox")!.Value.Split([' ', ',', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(n => double.Parse(n, CultureInfo.InvariantCulture)).ToArray();
            return new(svg, box[2] / box[3]);
        }
        catch (Exception error) when (error is InvalidDataException or XmlException or ArgumentException or InvalidOperationException)
        { return null; }
    }
}
