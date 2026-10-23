using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AIHub.Services.VectorGraphics;

namespace AIHub.Services.VectorEditor;

public static partial class VectorEditorSvg
{
    public static readonly XNamespace Ns = "http://www.w3.org/2000/svg";
    private static readonly HashSet<string> Shapes = new("g svg path rect circle ellipse line polyline polygon text".Split(' '));
    [GeneratedRegex("url\\(\\s*['\"]?#([^\\s)'\"]+)['\"]?\\s*\\)", RegexOptions.CultureInvariant)] private static partial Regex Reference();
    public static string Blank(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0 || width > 1e9 || height > 1e9)
            throw new InvalidDataException("Vector.Error.ViewBox");
        return new XElement(Ns + "svg", new XAttribute("viewBox", FormattableString.Invariant($"0 0 {width} {height}")), new XAttribute("data-lopata-document","1")).ToString(SaveOptions.DisableFormatting);
    }
    public static string Normalize(string svg)
    {
        var doc = VectorSvg.Parse(svg); var seen = new HashSet<string>();
        foreach (var element in doc.Root!.Descendants().Where(e => Shapes.Contains(e.Name.LocalName) && !e.Ancestors().Any(a => a.Name == Ns + "defs")))
        {
            var id = (string?)element.Attribute("data-lopata-id");
            if (id is null || !Guid.TryParseExact(id, "N", out _) || !seen.Add(id))
            { id = Guid.NewGuid().ToString("N"); element.SetAttributeValue("data-lopata-id", id); seen.Add(id); }
        }
        return doc.ToString(SaveOptions.DisableFormatting);
    }
    /// <summary>Import as a single module, preserving nested viewBox, inheritance, definitions and local references.</summary>
    public static string Insert(string canvas, string source, double x = 0, double y = 0)
    {
        var doc = VectorSvg.Parse(canvas); var imported = VectorSvg.Parse(source).Root!;
        if (!imported.Descendants(Ns+"metadata").Any() && (string?)imported.Attribute("data-lopata-document")!="1")
            imported.AddFirst(new XElement(Ns+"metadata",new OutputProvenance{Scenario="vector-editor/import",ModelHistory="unknown"}.Json()));
        RenameIds(imported);
        foreach (var e in imported.DescendantsAndSelf()) e.Attribute("data-lopata-id")?.Remove();
        var box = imported.Attribute("viewBox")!.Value.Split([' ', ',', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        imported.SetAttributeValue("width", box[2].ToString(CultureInfo.InvariantCulture)); imported.SetAttributeValue("height", box[3].ToString(CultureInfo.InvariantCulture));
        imported.SetAttributeValue("x", 0); imported.SetAttributeValue("y", 0);
        var module = new XElement(Ns + "g", new XAttribute("transform", FormattableString.Invariant($"translate({x} {y})")), imported);
        doc.Root!.Add(module); return Normalize(doc.ToString(SaveOptions.DisableFormatting));
    }
    public static string AsSheet(string source)
    {
        var root = VectorSvg.Parse(source).Root!;
        var doc = new XElement(Ns + "svg", new XAttribute("viewBox", root.Attribute("viewBox")!.Value));
        return Insert(doc.ToString(SaveOptions.DisableFormatting), source);
    }
    public static void RenameIds(XElement root)
    {
        var ids = root.DescendantsAndSelf().Attributes("id").ToDictionary(a => a.Value, _ => "v" + Guid.NewGuid().ToString("N"), StringComparer.Ordinal);
        foreach (var a in root.DescendantsAndSelf().Attributes().ToArray())
        {
            if (a.Name.LocalName == "id") a.Value = ids[a.Value];
            else if (a.Name.LocalName == "href" && a.Value.StartsWith('#') && ids.TryGetValue(a.Value[1..], out var target)) a.Value = "#" + target;
            else a.Value = Reference().Replace(a.Value, m => ids.TryGetValue(m.Groups[1].Value, out var id) ? "url(#" + id + ")" : m.Value);
        }
    }
    public static OutputProvenance Facts(IEnumerable<string> sources, DateTimeOffset created, string scenario = "vector-editor/svg-export")
    {
        var models = new List<OutputModelUse>(); var unknown = false;
        foreach (var svg in sources)
        {
            var doc = VectorSvg.Parse(svg);
            foreach (var meta in doc.Descendants(Ns + "metadata"))
            {
                try
                {
                    var facts = JsonSerializer.Deserialize<OutputProvenance>(meta.Value);
                    if (facts is null || facts.Program != "LOPATA") continue;
                    if (facts.Models is null || facts.Models.Any(m=>m is null) || facts.ModelHistory is null) {unknown=true;continue;}
                    _ = facts.Json(); models.AddRange(facts.Models); unknown |= facts.ModelHistory.Contains("unknown", StringComparison.OrdinalIgnoreCase);
                }
                catch (Exception e) when (e is JsonException or InvalidDataException) { unknown = true; }
            }
        }
        return new() { Scenario = scenario, CreatedUtc = created, Models = models.Distinct().ToArray(),
            ModelHistory = unknown ? "recorded-models; source-history-unknown" : models.Count == 0 ? "not-applicable" : "recorded" };
    }
    public static string Export(string svg, DateTimeOffset created) => VectorSvg.WithProvenance(svg, Facts([svg], created), null);
}
