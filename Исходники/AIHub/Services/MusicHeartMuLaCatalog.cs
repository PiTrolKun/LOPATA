using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

public static class MusicHeartMuLaCatalog
{
    public const string Variation = "music-heartmula3b";
    public const string Companions = "music-heartmula-codec";
    public const string RuntimeId = "runtime-music-heartmula";
    public const string SourceRevision = "5858ca8f1ffe58d62be7c007ea55f1033c5ca456";
    public const string ModelRevision = "41f6fc68490e11dc43fdabaa6b5767946408c903";
    public const string CompanionRevision = "f889dab0532cfa4bf459f2a3367eb6d346b8eeda";
    public const string ConfigRevision = "2e18e01702011f4f7dc8642b6260967df62417ea";
    public const string RuntimeRevision = "heartmula-5858ca8f-py312-1";
    public const string ModelName = "HeartMuLa";
    public static bool IsHeart(string id) => id == Variation;
    public static bool NoCircle(string id) => IsHeart(id) || MusicDiffRhythmCatalog.IsDiff(id);
    public static IReadOnlyList<ExpertParameter> Parameters { get; } = [
        new("seed", "Random", -1, -1, int.MaxValue),
        new("topk", "Sampling", 50, 1, 8197),
        new("temperature", "Sampling", 1, .01, 5, .01),
        new("cfg_scale", "Sampling", 3, 0, 20, .01),
        new("max_duration", "Duration", 240, 1, 360),
        new("lazy_load", "Memory", 1, 0, 1, Choices: ["Off", "On"]),
        new("mula_dtype", "Memory", 0, 0, 3, Choices: ["Auto", "BF16", "FP16", "FP32"]),
        new("codec_dtype", "Memory", 3, 0, 3, Choices: ["Auto", "BF16", "FP16", "FP32"]),
        new("codec_on_cpu", "Memory", 0, 0, 1, Choices: ["Same device", "CPU"])
    ];
    public static MusicExpertSettings Defaults() => new() { Variation = Variation,
        Values = Parameters.ToDictionary(p => p.Key, p => p.Default) };
    public static void Validate(MusicExpertSettings settings)
    {
        if (settings.Tuning is not null || settings.TextValues.Count != 0 || settings.Values.Count != Parameters.Count)
            throw new InvalidDataException("Unexpected HeartMuLa settings or tuning profile.");
        foreach (var p in Parameters)
            if (!settings.Values.TryGetValue(p.Key, out var v) || !double.IsFinite(v) || v < p.Minimum || v > p.Maximum || p.Step == 1 && v != Math.Truncate(v))
                throw new InvalidDataException("Invalid HeartMuLa parameter: " + p.Key);
    }
    public static IReadOnlyList<string> Components { get; } = [Variation, Companions, RuntimeId];
    public static IReadOnlyList<ManagedModelArtifactCard> Cards(string root)
    {
        var manifest = JsonSerializer.Deserialize<Files>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Tools", "heartmula-models.json")))!;
        return [Card(Variation, ModelRevision, manifest.Model, "HeartMuLa · 3B happy-new-year"),
            Card(Companions, CompanionRevision, manifest.Companions, "HeartCodec · 20260123"),
            Card(RuntimeId, SourceRevision, manifest.Runtime, "HeartMuLa · Python libraries")];
        ManagedModelArtifactCard Card(string id, string revision, List<ManagedModelArtifactFile> files, string name) => new() {
            ModelArtifactId = id, DisplayName = name, Family = ModelName, Role = ManagedModelRoles.Tool,
            Provider = id == RuntimeId ? "PyPI" : "Hugging Face", RepositoryId = id == Companions ? "HeartMuLa/HeartCodec-oss-20260123" : "HeartMuLa/HeartMuLa-oss-3B-happy-new-year", Revision = revision,
            Format = id == RuntimeId ? "Python wheels" : "Safetensors", License = id == RuntimeId ? "Apache-2.0; MIT; BSD; upstream notices" : "Apache-2.0",
            SourcePage = "https://github.com/HeartMuLa/heartlib/tree/" + SourceRevision,
            RuntimeBackend = "HeartMuLa author pipeline / PyTorch", IsManaged = true, CanRemoveFiles = true, ModelsRoot = root,
            InstallDirectory = string.IsNullOrWhiteSpace(root) ? "" : Path.Combine(root, "Music", id, revision[..12]),
            Origin = ManagedModelOrigins.PredefinedScenario,
            Consumers = [new() { Id = ScenarioNavigationCatalog.Music, DisplayName = "Музыка / Music", Kind = "scenario" }], Files = files
        };
    }
    private sealed record Files(List<ManagedModelArtifactFile> Model, List<ManagedModelArtifactFile> Companions, List<ManagedModelArtifactFile> Runtime);
}
