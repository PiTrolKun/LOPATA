using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lopata.Updates;

namespace AIHub.Services;

public sealed record EngineSetDefinition
{
    public string Id { get; init; } = "";
    public string Date { get; init; } = "";
    public string DescriptionKey { get; init; } = "";
    public string LimitKey { get; init; } = "";
    public string TechnicalDetails { get; init; } = "";
    public string[] SupportedCapabilities { get; init; } = [];
    public string[] LicenseIds { get; init; } = [];
    public required SignedManifest Manifest { get; init; }
    [JsonIgnore] public long DownloadBytes => VerifiedManifest.Packages.Sum(p => p.Size);
    [JsonIgnore] public long InstalledBytes => VerifiedManifest.Files.Sum(f => f.Size);
    [JsonIgnore] internal UpdateManifest VerifiedManifest { get; set; } = null!;
}

public sealed record EngineSetCatalog
{
    public int SchemaVersion { get; init; } = 1;
    public string RecommendedId { get; init; } = "002";
    public EngineSetDefinition[] Sets { get; init; } = [];
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 20, WriteIndented = true };

    public void Validate(IReadOnlyDictionary<string, string> trustedKeys)
    {
        if (SchemaVersion != 1 || Sets.Length is 0 or > 999)
            throw new InvalidDataException("Unsupported engine set catalog.");
        EngineSetSelection.ValidateId(RecommendedId);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var set in Sets)
        {
            EngineSetSelection.ValidateId(set.Id);
            if (!seen.Add(set.Id) || !DateOnly.TryParseExact(set.Date, "yyyy-MM-dd", out _)
                || string.IsNullOrWhiteSpace(set.DescriptionKey) || string.IsNullOrWhiteSpace(set.LimitKey)
                || set.SupportedCapabilities.Length == 0 || set.LicenseIds.Length == 0
                || set.LicenseIds.Any(string.IsNullOrWhiteSpace)
                || set.LicenseIds.Distinct(StringComparer.Ordinal).Count() != set.LicenseIds.Length)
                throw new InvalidDataException("Invalid engine set description.");
            var manifest = set.Manifest.Verify(trustedKeys);
            if (manifest.Files.Any(f => f.Root != "app")
                || manifest.Files.Any(f => f.Path.Split('/').Length < 3
                    || !new[] { "llama/cpu", "llama/vulkan", "sd/cpu", "sd/vulkan", "sd/cuda12", "studio/native" }
                        .Contains(string.Join('/', f.Path.Split('/').Take(2)), StringComparer.Ordinal)))
                throw new InvalidDataException("Engine files must remain inside their set.");
            var required = new List<string>();
            if (set.SupportedCapabilities.Contains("chat.gguf", StringComparer.Ordinal))
                required.AddRange(["llama/cpu/llama-server.exe", "llama/vulkan/llama-server.exe"]);
            if (set.SupportedCapabilities.Contains("image.z-image", StringComparer.Ordinal)
                || set.SupportedCapabilities.Contains("image.flux", StringComparer.Ordinal))
                required.AddRange(["sd/cpu/sd-cli.exe", "sd/vulkan/sd-cli.exe", "sd/cuda12/sd-cli.exe"]);
            if (set.SupportedCapabilities.Contains("music.studio", StringComparer.Ordinal))
                required.AddRange(["studio/native/music-server.exe", "studio/native/engine/yue-server.exe", "studio/native/manifest.json"]);
            if (required.Any(path => !manifest.Files.Any(f => f.Path == path)))
                throw new InvalidDataException("The engine set is missing a required runtime entry point.");
            set.VerifiedManifest = manifest;
        }
        if (!seen.Contains(RecommendedId)) throw new InvalidDataException("Missing recommended engine set.");
    }
}

public sealed record EngineSetState(string ActiveId, bool ManualSelection = false, string? DeclinedRecommendation = null);
