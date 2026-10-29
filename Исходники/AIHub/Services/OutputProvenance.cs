using System.IO;
using System.Text.Json;
using System.Xml.Linq;
using AIHub.Models;

namespace AIHub.Services;

public sealed record OutputModelUse(string Id, string Name, string Revision, string Role);

/// <summary>Public output facts only. Never contains prompts, local paths or diagnostic logs.</summary>
public sealed record OutputProvenance
{
    public int Schema { get; init; } = 1;
    public string Program { get; init; } = "LOPATA";
    // Generated from the same VERSION property as AssemblyInformationalVersion.
    // Deserializing every history entry must not repeatedly enter the native type loader.
    public string ProgramVersion { get; init; } = ApplicationBuildVersion.Value;
    public string Scenario { get; init; } = "";
    public DateTimeOffset ExportedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CreatedUtc { get; init; }
    public double? GenerationSeconds { get; init; }
    public string ModelHistory { get; init; } = "not-applicable";
    public string ModelScope { get; init; } = "output";
    public OutputModelUse[] Models { get; init; } = [];
    public static OutputProvenance Capture(string kind) => new() { Scenario = "screen-capture/" + kind, CreatedUtc = DateTimeOffset.UtcNow };
    public string Json()
    {
        if (GenerationSeconds is { } seconds && (!double.IsFinite(seconds) || seconds < 0))
            throw new InvalidDataException("Invalid generation duration.");
        foreach (var model in Models)
            foreach (var value in new[] { model.Id, model.Name, model.Revision, model.Role })
                if (value is null || value.Length > 1024 || Path.IsPathFullyQualified(value) || value.Any(char.IsControl))
                    throw new InvalidDataException("Invalid public model identity.");
        var json = JsonSerializer.Serialize(this);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 48_000) throw new InvalidDataException("Output provenance is too large.");
        return json;
    }
    public string Xmp()
    {
        XNamespace x = "adobe:ns:meta/", rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#",
            xmp = "http://ns.adobe.com/xap/1.0/", lopata = "https://lopata.app/ns/provenance/1.0/";
        return new XDocument(new XElement(x + "xmpmeta", new XAttribute(XNamespace.Xmlns + "x", x),
            new XElement(rdf + "RDF", new XAttribute(XNamespace.Xmlns + "rdf", rdf),
                new XElement(rdf + "Description", new XAttribute(rdf + "about", ""),
                    new XAttribute(XNamespace.Xmlns + "xmp", xmp), new XAttribute(XNamespace.Xmlns + "lopata", lopata),
                    new XElement(xmp + "CreatorTool", "LOPATA " + ProgramVersion),
                    new XElement(lopata + "Provenance", Json()))))).ToString(SaveOptions.DisableFormatting);
    }

    public static OutputModelUse[] SessionModels(ImageAnalysisLiterarySession session) =>
        string.IsNullOrWhiteSpace(session.ModelId) ? [] : [new(session.ModelId, session.ModelId, session.ModelRevision, "image analysis and description")];

    public static OutputProvenance DescriptionSpeech(ImageAnalysisLiterarySession? session, string actualProvider)
    {
        var facts = session is null ? new OutputProvenance { ModelHistory = "unknown" } : ImageDescription(session);
        return facts with { Scenario = "image-description/speech", Models = facts.Models.Append(new OutputModelUse(actualProvider, actualProvider, "", "speech synthesis; revision unknown")).ToArray() };
    }
    public static OutputProvenance ImageDescription(ImageAnalysisLiterarySession session) => new()
    {
        Scenario = "image-description/" + session.SubscenarioId, CreatedUtc = session.CreatedAt.ToUniversalTime(),
        // The persisted session identifies the executor, not a selection in today's settings.
        ModelHistory = session.GetSelectedVersion()?.Models.Length > 0 ? "recorded-version; auxiliary-history-incomplete"
            : string.IsNullOrWhiteSpace(session.ModelId) ? "unknown" : "legacy-session; auxiliary-history-incomplete",
        Models = session.GetSelectedVersion()?.Models is { Length: > 0 } models ? models : SessionModels(session)
    };
}
