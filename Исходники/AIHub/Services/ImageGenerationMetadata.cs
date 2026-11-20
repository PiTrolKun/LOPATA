using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using AIHub.Models;
using AIHub.Services.VisualScenario;

namespace AIHub.Services;

/// <summary>Only the submitted author and generation data belong in a shared image.</summary>
public static class ImageGenerationMetadata
{
    private static readonly XNamespace X = "adobe:ns:meta/";
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace Exif = "http://ns.adobe.com/exif/1.0/";
    private static readonly XNamespace Lopata = "https://lopata.app/ns/provenance/1.0/";
    private static readonly string[] GenerationFields =
    [
        "ProgramVersion", "Scenario", "OperationId", "Model", "ModelId", "ModelRevision", "Prompt",
        "EngineSetId", "EngineSetDate", "Backend", "BackendCommit", "Steps", "Sampler", "Cfg", "Seed",
        "Width", "Height", "OutputWidth", "OutputHeight", "OutputFormat", "ReferenceImageCount"
    ];

    public static string Author(string? name) => string.IsNullOrWhiteSpace(name) ? "LOPATA User" : name.Trim();

    public static IReadOnlyDictionary<string, string> Create(ImageGenerationRequest request, int index, DateTimeOffset createdAt,
        ImageGenerationRuntimeReceipt? runtime = null)
    {
        var model = ImageGenerationCatalog.Get(request.ModelId);
        var senseNova = model.Id == VisualSenseNova.Id;
        if (senseNova) runtime = VisualSenseNova.NormalizeReceipt(runtime);
        var engineSetId = senseNova ? null : runtime?.EngineSetId;
        var author = Author(request.Metadata?.Author);
        var title = $"{model.Name} #{request.FirstGenerationNumber + index}";
        var version = typeof(ImageGenerationMetadata).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        var producer = model.Id switch
        {
            VisualSenseNova.Id => $"LOPATA {version}, {VisualSenseNova.Backend}",
            MiniMaxImageRuntime.ModelId => $"LOPATA {version}, stable-diffusion.cpp",
            _ => EngineSetMetadataIdentity.Producer(version, engineSetId)
        };
        var engineSetDate = EngineSetMetadataIdentity.Date(engineSetId);
        var seed = request.Seeds[index];
        var keywords = new[] { "LOPATA", "AI", model.Name };
        var size = $"{request.Width}x{request.Height}";
        var parameters = request.Prompt + "\n" + FormattableString.Invariant(
            $"Steps: {model.Steps}, Sampler: {model.Sampler}, CFG scale: {model.Cfg}, Seed: {seed}, Size: {size}, Model: {model.Name}");
        if (model.ClipSkip > 0) parameters += FormattableString.Invariant($", Clip skip: {model.ClipSkip}");
        if (model.Id == "krea") parameters += ", Flow shift: 1.15";
        if (runtime?.Schedule is { } schedule) parameters += ", Schedule: " + schedule;
        if (runtime?.Loras.Length > 0) parameters += ", LoRA: " + string.Join("; ", runtime.Loras.Select(l =>
            l.Repository + "@" + l.Revision + ":" + l.Strength.ToString(CultureInfo.InvariantCulture)));

        XNamespace dc = "http://purl.org/dc/elements/1.1/";
        XNamespace xmp = "http://ns.adobe.com/xap/1.0/";
        XElement Alternative(XName name, string value) => new(name, new XElement(Rdf + "Alt",
            new XElement(Rdf + "li", new XAttribute(XNamespace.Xml + "lang", "x-default"), value)));
        var xml = new XElement(X + "xmpmeta",
            new XElement(Rdf + "RDF", new XElement(Rdf + "Description", new XAttribute(Rdf + "about", ""),
                new XAttribute(XNamespace.Xmlns + "dc", dc), new XAttribute(XNamespace.Xmlns + "xmp", xmp),
                Alternative(dc + "title", title),
                new XElement(dc + "creator", new XElement(Rdf + "Seq", new XElement(Rdf + "li", author))),
                Alternative(dc + "description", request.Prompt), Alternative(Exif + "UserComment", request.Prompt),
                new XElement(dc + "subject", new XElement(Rdf + "Bag", keywords.Select(k => new XElement(Rdf + "li", k)))),
                new XElement(xmp + "CreatorTool", "LOPATA"),
                new XElement(xmp + "CreateDate", createdAt.ToString("O", CultureInfo.InvariantCulture)),
                new XElement(Exif + "DateTimeOriginal", createdAt.ToString("O", CultureInfo.InvariantCulture)))));
        var structured = JsonSerializer.SerializeToNode(new
        {
            Schema = 1, Author = author, Program = "LOPATA", ProgramVersion = version,
            Scenario = request.Metadata?.Scenario ?? "image-generation/simple", OperationId = request.Id,
            EngineSetId = engineSetId, EngineSetDate = engineSetDate,
            Model = model.Name, ModelId = model.Id, ModelRevision = runtime?.ArtistRevision ?? request.Metadata?.ArtistRevision,
            AdditionalModels = request.Metadata?.AdditionalModels ?? [], GenerationNumber = request.FirstGenerationNumber + index,
            request.Prompt, request.SubmittedAt, CreatedAt = createdAt, Seed = seed, request.Width, request.Height,
            OutputWidth = request.Width, OutputHeight = request.Height, OutputLongestSide = 0, ResizeAlgorithm = "None",
            OutputFormat = "PNG", ReferenceImageCount = request.ReferenceImages.Length,
            model.Steps, model.Cfg, model.Sampler, model.ClipSkip,
            FlowShift = model.Id == "krea" ? (double?)1.15 : null,
            Backend = senseNova ? VisualSenseNova.Backend : "stable-diffusion.cpp", BackendCommit = runtime?.BackendCommit,
            Runtime = runtime, RuntimeHistory = runtime is null ? "unknown" : "executed-receipt",
            AppliedLoras = runtime?.Loras ?? [],
            LoraPrompt = runtime?.Loras.Length > 0 ? string.Join(", ", runtime.Loras.Select(l => l.Trigger).Where(t => t.Length > 0 && !request.Prompt.Contains(t, StringComparison.OrdinalIgnoreCase))
                .Distinct().Append(request.Prompt)) : null,
            DiffusionFlashAttention = senseNova ? (bool?)null : true,
            OffloadToCpu = runtime is null ? (bool?)null
                : runtime.Placement == (senseNova ? "balanced" : nameof(ImageGenerationGpuPlacement.Hybrid)),
            VaeTiling = senseNova ? (bool?)null : true,
            // Engine delivery is recorded above from the actual attempt; static model weights stay separate.
            Components = model.Components.Where(id => !ImageGenerationCatalog.IsRuntimeArtifact(id)).Select(id =>
            {
                var artifact = id == VisualSenseNova.Id ? VisualSenseNova.Artifact
                    : ImageGenerationCatalog.Manifest.Artifacts.Single(a => a.Id == id);
                return new { artifact.Id, artifact.Repository, artifact.Revision,
                    Files = artifact.Files.Select(f => new { f.RelativePath, f.Sha256 }) };
            })
        })!.AsObject();
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["XML:com.adobe.xmp"] = WriteXmp(xml, structured, parameters, request.Width, request.Height),
            ["Title"] = title, ["Author"] = author, ["Description"] = request.Prompt, ["Prompt"] = request.Prompt,
            ["Software"] = "LOPATA", ["Creation Time"] = createdAt.ToString("O", CultureInfo.InvariantCulture),
            ["LOPATA_PRODUCER"] = producer,
            ["Keywords"] = string.Join("; ", keywords), ["parameters"] = parameters, ["LOPATA"] = structured.ToJsonString()
        };
        if (!senseNova) EngineSetMetadataIdentity.AddTags(fields, engineSetId);
        return fields;
    }

    public static IReadOnlyDictionary<string, string> ForOutput(IReadOnlyDictionary<string, string> original,
        ImageGenerationRequest request, int width, int height, string algorithm)
    {
        var fields = original.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        JsonObject? data = null;
        if (fields.TryGetValue("LOPATA", out var structured))
        {
            data = JsonNode.Parse(structured) as JsonObject ?? throw new JsonException("Invalid generation metadata.");
            data["OutputWidth"] = width; data["OutputHeight"] = height;
            data["OutputLongestSide"] = request.OutputLongestSide; data["ResizeAlgorithm"] = algorithm;
            data["OutputFormat"] = ImageGenerationOutput.FormatId(request.OutputFormat).ToUpperInvariant();
            fields["LOPATA"] = data.ToJsonString();
        }
        if (fields.TryGetValue("parameters", out var parameters))
        {
            parameters += FormattableString.Invariant($", Output size: {width}x{height}, Resize: {algorithm}");
            fields["parameters"] = parameters;
        }
        if (fields.TryGetValue("XML:com.adobe.xmp", out var xmp))
            fields["XML:com.adobe.xmp"] = WriteXmp(XElement.Parse(xmp), data, parameters, width, height);
        return fields;
    }

    private static string WriteXmp(XElement packet, JsonObject? data, string? parameters, int width, int height)
    {
        // Explicit conventional prefixes allow readers such as ExifTool to recognize the XMP envelope.
        packet.Attribute("xmlns")?.Remove();
        packet.SetAttributeValue(XNamespace.Xmlns + "x", X);
        var rdf = packet.Descendants(Rdf + "RDF").First();
        rdf.Attribute("xmlns")?.Remove();
        rdf.SetAttributeValue(XNamespace.Xmlns + "rdf", Rdf);
        var description = rdf.Element(Rdf + "Description")!;
        description.SetAttributeValue(XNamespace.Xmlns + "exif", Exif);
        description.SetAttributeValue(XNamespace.Xmlns + "lopata", Lopata);
        description.SetElementValue(Exif + "PixelXDimension", width);
        description.SetElementValue(Exif + "PixelYDimension", height);
        description.SetElementValue(Lopata + "Parameters", parameters);
        if (data is not null)
        {
            description.SetElementValue(Lopata + "Generation", data.ToJsonString());
            foreach (var field in GenerationFields)
                description.SetElementValue(Lopata + field, data[field]?.ToString());
            var runtime = data["Runtime"] as JsonObject;
            description.SetElementValue(Lopata + "ComputeBackend", runtime?["Backend"]?.ToString());
            description.SetElementValue(Lopata + "Schedule", runtime?["Schedule"]?.ToString());
        }
        return packet.ToString(SaveOptions.DisableFormatting);
    }
}
