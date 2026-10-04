using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Only the submitted author and generation data belong in a shared image.</summary>
public static class ImageGenerationMetadata
{
    public static string Author(string? name) => string.IsNullOrWhiteSpace(name) ? "LOPATA User" : name.Trim();

    public static IReadOnlyDictionary<string, string> Create(ImageGenerationRequest request, int index, DateTimeOffset createdAt)
    {
        var model = ImageGenerationCatalog.Get(request.ModelId);
        var author = Author(request.Metadata?.Author);
        var title = $"{model.Name} #{request.FirstGenerationNumber + index}";
        var version = typeof(ImageGenerationMetadata).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        var seed = request.Seeds[index];
        var keywords = new[] { "LOPATA", "AI", model.Name };
        var size = $"{request.Width}x{request.Height}";
        var parameters = request.Prompt + "\n" + FormattableString.Invariant(
            $"Steps: {model.Steps}, Sampler: {model.Sampler}, CFG scale: {model.Cfg}, Seed: {seed}, Size: {size}, Model: {model.Name}");
        if (model.ClipSkip > 0) parameters += FormattableString.Invariant($", Clip skip: {model.ClipSkip}");
        if (model.Id == "krea") parameters += ", Flow shift: 1.15";

        XNamespace rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
        XNamespace dc = "http://purl.org/dc/elements/1.1/";
        XNamespace xmp = "http://ns.adobe.com/xap/1.0/";
        XNamespace exif = "http://ns.adobe.com/exif/1.0/";
        XElement Alternative(XName name, string value) => new(name, new XElement(rdf + "Alt",
            new XElement(rdf + "li", new XAttribute(XNamespace.Xml + "lang", "x-default"), value)));
        var xml = new XElement(XName.Get("xmpmeta", "adobe:ns:meta/"),
            new XElement(rdf + "RDF", new XElement(rdf + "Description", new XAttribute(rdf + "about", ""),
                new XAttribute(XNamespace.Xmlns + "dc", dc), new XAttribute(XNamespace.Xmlns + "xmp", xmp), new XAttribute(XNamespace.Xmlns + "exif", exif),
                Alternative(dc + "title", title),
                new XElement(dc + "creator", new XElement(rdf + "Seq", new XElement(rdf + "li", author))),
                Alternative(dc + "description", request.Prompt), Alternative(exif + "UserComment", request.Prompt),
                new XElement(dc + "subject", new XElement(rdf + "Bag", keywords.Select(k => new XElement(rdf + "li", k)))),
                new XElement(xmp + "CreatorTool", "LOPATA"),
                new XElement(xmp + "CreateDate", createdAt.ToString("O", CultureInfo.InvariantCulture)),
                new XElement(exif + "DateTimeOriginal", createdAt.ToString("O", CultureInfo.InvariantCulture)))));
        var structured = JsonSerializer.Serialize(new
        {
            Schema = 1, Author = author, Program = "LOPATA", ProgramVersion = version,
            Model = model.Name, ModelId = model.Id, GenerationNumber = request.FirstGenerationNumber + index,
            request.Prompt, request.SubmittedAt, CreatedAt = createdAt, Seed = seed, request.Width, request.Height,
            OutputWidth = request.Width, OutputHeight = request.Height, OutputLongestSide = 0, ResizeAlgorithm = "None",
            model.Steps, model.Cfg, model.Sampler, model.ClipSkip,
            FlowShift = model.Id == "krea" ? (double?)1.15 : null,
            Backend = "stable-diffusion.cpp", ImageGenerationCatalog.Manifest.BackendCommit,
            DiffusionFlashAttention = true, OffloadToCpu = true, VaeTiling = true,
            Components = model.Components.Select(id =>
            {
                var artifact = ImageGenerationCatalog.Manifest.Artifacts.Single(a => a.Id == id);
                return new { artifact.Id, artifact.Repository, artifact.Revision,
                    Files = artifact.Files.Select(f => new { f.RelativePath, f.Sha256 }) };
            })
        });
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["XML:com.adobe.xmp"] = xml.ToString(SaveOptions.DisableFormatting),
            ["Title"] = title, ["Author"] = author, ["Description"] = request.Prompt,
            ["Software"] = "LOPATA", ["Creation Time"] = createdAt.ToString("O", CultureInfo.InvariantCulture),
            ["Keywords"] = string.Join("; ", keywords), ["parameters"] = parameters, ["LOPATA"] = structured
        };
    }

    public static IReadOnlyDictionary<string, string> ForOutput(IReadOnlyDictionary<string, string> original,
        ImageGenerationRequest request, int width, int height, string algorithm)
    {
        var fields = original.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        if (fields.TryGetValue("LOPATA", out var structured))
        {
            var json = JsonNode.Parse(structured)?.AsObject() ?? throw new JsonException("Invalid generation metadata.");
            json["OutputWidth"] = width; json["OutputHeight"] = height;
            json["OutputLongestSide"] = request.OutputLongestSide; json["ResizeAlgorithm"] = algorithm;
            fields["LOPATA"] = json.ToJsonString();
        }
        if (fields.TryGetValue("parameters", out var parameters))
            fields["parameters"] = parameters + FormattableString.Invariant($", Output size: {width}x{height}, Resize: {algorithm}");
        if (fields.TryGetValue("XML:com.adobe.xmp", out var xmp))
        {
            var xml = XElement.Parse(xmp); XNamespace rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
            XNamespace exif = "http://ns.adobe.com/exif/1.0/";
            var description = xml.Descendants(rdf + "Description").First();
            description.SetElementValue(exif + "PixelXDimension", width);
            description.SetElementValue(exif + "PixelYDimension", height);
            fields["XML:com.adobe.xmp"] = xml.ToString(SaveOptions.DisableFormatting);
        }
        return fields;
    }
}
