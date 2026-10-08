using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Stable identities shared by projects, downloads, presets and durable jobs.</summary>
public static class MusicModelVariants
{
    public const string Bf16 = "music-yue2-bf16";
    public const string Bf16Vae = "music-yue2-bf16-vae";
    public const string Bf16Revision = "8f24312f187763b9854adeec87e437e2b03bbff1";
    public const string VaeRevision = "152733a19ad43aa67e367f9b5503ef8075bb5126";
    public const string RuntimeRevision = "yue2-infer-0.1.5-lopata-1";
    public const string RuntimeId = "runtime-music-yue2-bf16";
    public static string Normalize(string id) => id switch { "q8" => MusicComponentCatalog.ModelId, "bf16" => Bf16, _ => id };
    public static bool Supported(string id) => id is MusicComponentCatalog.ModelId or Bf16;
    public static string Revision(string id) => id == Bf16 ? Bf16Revision : MusicComponentCatalog.Revision;
    public static string Name(string id) => id == Bf16 ? "YuE2 3B BF16" : "YuE2 3B Q8_0";
    public static string Decoder(string id) => id == Bf16 ? Bf16Vae : MusicComponentCatalog.DecoderId;
    public static IReadOnlyList<string> Components(string id) => id == Bf16 ? [id, Decoder(id), RuntimeId] : [id, Decoder(id)];
    public static bool SupportsParameter(string id, string key) => id != Bf16 || key is not ("lm_seed" or "peak_clip");
    public static MusicExpertSettings Defaults(string id)
    {
        var settings = new MusicExpertSettings { Variation = id };
        if (id != Bf16) return settings;
        // Accepted listening sample; seeds remain automatic for ordinary use.
        settings.Values["cfg_scale"] = 1.9;
        settings.Values["abc_sampling.temperature"] = .576278;
        settings.Values["abc_sampling.top_p"] = .841022;
        settings.Values["abc_sampling.top_k"] = 20;
        settings.Values["semantic_sampling.temperature"] = .913074;
        settings.Values["semantic_sampling.top_p"] = .923922;
        settings.Values["semantic_sampling.top_k"] = 78;
        return settings with { Tuning = new() { Profile = MusicTuningProfile.Bf16Id,
            X = -.7372220341089587, Y = -.4346309007060279 } };
    }
    public static MusicExpertSettings Transfer(MusicExpertSettings source, string variation)
    {
        source.Validate(); var target = Defaults(variation);
        foreach (var pair in source.Values.Where(p => SupportsParameter(source.Variation, p.Key) && SupportsParameter(variation, p.Key)))
            target.Values[pair.Key] = pair.Value;
        target.Validate(); return target;
    }
    public static IReadOnlyList<ManagedModelArtifactCard> Cards(string root, string id)
    {
        if (id == MusicComponentCatalog.ModelId) return MusicComponentCatalog.CreateCards(root);
        if (id != Bf16) throw new InvalidDataException("Unsupported music model variation.");
        var manifest = JsonSerializer.Deserialize<Bf16Files>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Tools", "yue2-bf16-models.json")))!;
        return [Card(Bf16, "m-a-p/YuE2-3B", Bf16Revision, manifest.Model), Card(Bf16Vae, "m-a-p/YuE2-Vae", VaeRevision, manifest.Vae),
            Card(RuntimeId, "m-a-p/YuE2-3B", Bf16Revision, manifest.Runtime)];
        ManagedModelArtifactCard Card(string key, string repo, string revision, List<ManagedModelArtifactFile> files) => new() {
            ModelArtifactId = key, DisplayName = key == Bf16 ? "YuE2 3B BF16 · Safetensors" : key == RuntimeId ? "YuE2 BF16 · Python libraries" : "YuE2 VAE F32 · Safetensors",
            Family = "YuE2", Role = ManagedModelRoles.Tool, Provider = "Hugging Face", RepositoryId = repo, Revision = revision,
            Format = key == RuntimeId ? "Python wheels" : "Safetensors", License = key == RuntimeId ? "Bundled upstream licenses" : "CC BY-NC 4.0", SourcePage = "https://huggingface.co/" + repo,
            RuntimeBackend = "yue2-infer/PyTorch", IsManaged = true, CanRemoveFiles = true, ModelsRoot = root,
            InstallDirectory = string.IsNullOrWhiteSpace(root) ? "" : Path.Combine(root, "Music", key, revision[..12]),
            Origin = ManagedModelOrigins.PredefinedScenario,
            Consumers = [new() { Id = ScenarioNavigationCatalog.Music, DisplayName = "Музыка / Music", Kind = "scenario" }], Files = files
        };
    }
    private sealed record Bf16Files(List<ManagedModelArtifactFile> Model, List<ManagedModelArtifactFile> Vae, List<ManagedModelArtifactFile> Runtime);
    public static string Artifact(string root, string variation, string id) {
        var card = Cards(root, variation).Single(c => c.ModelArtifactId == id);
        return Path.Combine(card.InstallDirectory, card.Files.Single(f => f.RelativePath.EndsWith(variation == Bf16 ? ".safetensors" : ".gguf", StringComparison.Ordinal)).RelativePath);
    }
}
