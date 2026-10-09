using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Pinned author pipeline, with a separately disclosed experimental Russian frontend.</summary>
public static class MusicDiffRhythmCatalog
{
    public const string Variation = "music-diffrhythm2";
    public const string Companions = "music-diffrhythm2-companions";
    public const string RuntimeId = "runtime-music-diffrhythm2";
    public const string SourceRevision = "0563fcec4bdf42ca33f6e76ebe9949429d07bf00";
    public const string ModelRevision = "9aa15742e4889c0eb2e198db6fdab1facf1b6761";
    public const string CompanionRevision = "2e01c796b71dca71b45251384c04cd7b237c9020";
    public const string RuntimeRevision = "diff2-0563fcec-py312-1";
    public const string ModelName = "DiffRhythm 2";
    public const int MaximumDuration = 240;
    public static bool IsDiff(string id) => id == Variation;
    public static IReadOnlyList<ExpertParameter> Parameters { get; } = [
        new("seed", "Random", -1, -1, int.MaxValue),
        new("steps", "Diffusion", 16, 1, 100),
        new("cfg", "Diffusion", 1.3, 1, 10, .01),
        new("solver", "Diffusion", 0, 0, 3, Choices: ["euler", "midpoint", "rk4", "implicit_adams"]),
        new("fake_stereo", "Audio", 1, 0, 1),
        new("experimental_ru", "Text", 1, 0, 1)
    ];
    public static MusicExpertSettings Defaults() => new() { Variation = Variation,
        Values = Parameters.ToDictionary(p => p.Key, p => p.Default) };
    public static void Validate(MusicExpertSettings settings)
    {
        if (settings.Tuning is not null || settings.TextValues.Count != 0 || settings.Values.Count != Parameters.Count)
            throw new InvalidDataException("Unexpected DiffRhythm settings or tuning profile.");
        foreach (var p in Parameters)
            if (!settings.Values.TryGetValue(p.Key, out var v) || !double.IsFinite(v) || v < p.Minimum || v > p.Maximum || p.Step == 1 && v != Math.Truncate(v))
                throw new InvalidDataException("Invalid DiffRhythm parameter: " + p.Key);
    }
    public static IReadOnlyList<string> Components { get; } = [Variation, Companions, RuntimeId, "runtime.espeak"];
    public static IReadOnlyList<ManagedModelArtifactCard> Cards(string root)
    {
        var manifest = JsonSerializer.Deserialize<Files>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Tools", "diffrhythm2-models.json")))!;
        return [Card(Variation, ModelRevision, manifest.Model, "DiffRhythm 2 · model + BigVGAN"),
            Card(Companions, CompanionRevision, manifest.Companions, "DiffRhythm · MuQ + XLM-RoBERTa + G2P"),
            Card(RuntimeId, SourceRevision, manifest.Runtime, "DiffRhythm · Python libraries")];
        ManagedModelArtifactCard Card(string id, string revision, List<ManagedModelArtifactFile> files, string name) => new() {
            ModelArtifactId = id, DisplayName = name, Family = ModelName, Role = ManagedModelRoles.Tool,
            Provider = id == RuntimeId ? "PyPI" : "Hugging Face", RepositoryId = "ASLP-lab/DiffRhythm2", Revision = revision,
            Format = id == RuntimeId ? "Python wheels" : "Safetensors / PyTorch", License = id == Variation ? "Apache-2.0; MIT BigVGAN" :
                id == Companions ? "CC-BY-NC-4.0 MuQ weights; MIT; upstream notices" : "Apache-2.0; MIT; BSD; upstream notices",
            SourcePage = "https://huggingface.co/spaces/ASLP-lab/DiffRhythm2/tree/" + SourceRevision,
            RuntimeBackend = "DiffRhythm 2 author pipeline / PyTorch", IsManaged = true, CanRemoveFiles = true, ModelsRoot = root,
            InstallDirectory = string.IsNullOrWhiteSpace(root) ? "" : Path.Combine(root, "Music", id, revision[..12]),
            Origin = ManagedModelOrigins.PredefinedScenario,
            Consumers = [new() { Id = ScenarioNavigationCatalog.Music, DisplayName = "Музыка / Music", Kind = "scenario" }], Files = files
        };
    }
    private sealed record Files(List<ManagedModelArtifactFile> Model, List<ManagedModelArtifactFile> Companions, List<ManagedModelArtifactFile> Runtime);
}
