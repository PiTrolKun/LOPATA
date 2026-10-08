using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Capabilities of the pinned official XL Turbo API. YuE settings never enter this contract.</summary>
public static class MusicAceCatalog
{
    public const string Variation = "music-ace15-xl-turbo";
    public const string Companions = "music-ace15-xl-companions";
    public const string RuntimeId = "runtime-music-ace15-xl";
    public const string SourceRevision = "ca1e85fe9430179831e6bc6be790c332190a3866";
    public const string ModelRevision = "d4a0b288b83ebb7e25a8c0b32c573c22e134e8ee";
    public const string CompanionRevision = "19671f406d603126926c1b7e2adc169acbcade22";
    public const string RuntimeRevision = "ace15-xl-ca1e85fe-py312-1";
    public const string ModelName = "ACE-Step 1.5";
    public const int MaximumInferenceSteps = 20;
    public static bool IsAce(string id) => id == Variation;
    public static IReadOnlyList<ExpertParameter> Parameters { get; } = [
        new("bpm", "Conditions", 0, 0, 300),
        new("seed", "Random", -1, -1, int.MaxValue),
        new("inference_steps", "Diffusion", 8, 1, MaximumInferenceSteps),
        new("shift", "Diffusion", 1, 1, 5, .01),
        new("infer_method", "Diffusion", 0, 0, 1, Choices: ["ode", "sde"]),
        new("sampler_mode", "Diffusion", 0, 0, 1, Choices: ["euler", "heun"]),
        new("velocity_norm_threshold", "Diffusion", 0, 0, 100, .01),
        new("velocity_ema_factor", "Diffusion", 0, 0, 1, .01),
        new("thinking", "LanguageModel", 1, 0, 1),
        new("use_cot_metas", "LanguageModel", 1, 0, 1),
        new("use_cot_caption", "LanguageModel", 1, 0, 1),
        new("use_cot_language", "LanguageModel", 1, 0, 1),
        new("use_constrained_decoding", "LanguageModel", 1, 0, 1),
        new("lm_temperature", "LanguageModel", .85, 0, 2, .01),
        new("lm_cfg_scale", "LanguageModel", 2, 1, 10, .01),
        new("lm_top_k", "LanguageModel", 0, 0, 1000),
        new("lm_top_p", "LanguageModel", .9, .01, 1, .01),
        new("dcw_enabled", "Wavelet", -1, -1, 1, Choices: ["Auto", "Off", "On"]),
        new("dcw_scaler", "Wavelet", .05, 0, 1, .001),
        new("dcw_high_scaler", "Wavelet", .02, 0, 1, .001),
        new("enable_normalization", "Audio", 1, 0, 1),
        new("normalization_db", "Audio", -1, -30, 0, .1),
        new("fade_in_duration", "Audio", 0, 0, 30, .1),
        new("fade_out_duration", "Audio", 0, 0, 30, .1),
        new("latent_shift", "Audio", 0, -10, 10, .01),
        new("latent_rescale", "Audio", 1, .01, 10, .01),
        new("retake_seed", "Retake", -1, -1, int.MaxValue),
        new("retake_variance", "Retake", 0, 0, 1, .01),
        new("offload_to_cpu", "Runtime", -1, -1, 1, Choices: ["Auto", "Off", "On"]),
        new("offload_dit_to_cpu", "Runtime", -1, -1, 1, Choices: ["Auto", "Off", "On"]),
        new("lm_offload_to_cpu", "Runtime", -1, -1, 1, Choices: ["Auto", "Off", "On"]),
        new("use_flash_attention", "Runtime", 0, 0, 1),
        new("compile_model", "Runtime", 0, 0, 1),
        new("constrained_decoding_debug", "Runtime", 0, 0, 1)
    ];
    public sealed record TextParameter(string Key, string Group, string Default, string[]? Choices = null);
    public static IReadOnlyList<TextParameter> TextParameters { get; } = [
        new("vocal_language", "Conditions", "auto", ["auto", "unknown", "ar", "az", "bg", "bn", "ca", "cs", "da", "de", "el", "en",
            "es", "fa", "fi", "fr", "he", "hi", "hr", "ht", "hu", "id", "is", "it", "ja", "ko", "la", "lt", "ms", "ne", "nl", "no",
            "pa", "pl", "pt", "ro", "ru", "sa", "sk", "sr", "sv", "sw", "ta", "te", "th", "tl", "tr", "uk", "ur", "vi", "yue", "zh"]),
        new("timesignature", "Conditions", "auto", ["auto", "", "2", "3", "4", "6"]),
        new("keyscale", "Conditions", ""),
        new("timesteps", "Diffusion", ""),
        new("lm_negative_prompt", "LanguageModel", ""),
        new("dcw_mode", "Wavelet", "double", ["low", "high", "double", "pix"]),
        new("dcw_wavelet", "Wavelet", "haar", ["haar", "db2", "db4", "sym4", "sym8", "coif2"])
    ];
    public static MusicExpertSettings Defaults() => new() { Variation = Variation,
        Values = Parameters.ToDictionary(p => p.Key, p => p.Default),
        TextValues = TextParameters.ToDictionary(p => p.Key, p => p.Default) };
    public static void Validate(MusicExpertSettings settings)
    {
        settings.Tuning?.Validate();
        if (settings.Tuning is not null && settings.Tuning.Profile != MusicAceTuningProfile.Id)
            throw new InvalidDataException("ACE circle profile belongs to another model.");
        if (settings.Values.Count != Parameters.Count || settings.TextValues.Count != TextParameters.Count)
            throw new InvalidDataException("Incomplete or unknown ACE parameters.");
        foreach (var p in Parameters) {
            // Read old projects/presets without discarding them. New editor input is bounded separately;
            // the request builder records the upstream clamp for legacy values.
            var maximum = p.Key == "inference_steps" ? 200 : p.Maximum;
            if (!settings.Values.TryGetValue(p.Key, out var v) || !double.IsFinite(v) || v < p.Minimum || v > maximum || p.Step == 1 && v != Math.Truncate(v))
                throw new InvalidDataException("Invalid ACE parameter: " + p.Key);
        }
        if (settings.Get("bpm") is > 0 and < 30) throw new InvalidDataException("ACE bpm: 0 = auto; otherwise 30–300.");
        if (settings.Get("use_flash_attention") != 0) throw new InvalidDataException("flash-attn is not included in the Windows ACE runtime.");
        foreach (var p in TextParameters) {
            if (!settings.TextValues.TryGetValue(p.Key, out var text) || text is null || text.Length > 4096 || text.Contains('\0') || p.Choices is { } choices && !choices.Contains(text))
                throw new InvalidDataException("Invalid ACE parameter: " + p.Key);
        }
        if (settings.TextValues["keyscale"] is { Length: > 0 } key && !System.Text.RegularExpressions.Regex.IsMatch(key, @"^[A-G](?:#|b|♯|♭)? (?:major|minor)$"))
            throw new InvalidDataException("keyscale: C major, F# minor, etc.; empty = automatic.");
        double[]? steps;
        try { steps = ParseTimesteps(settings.TextValues["timesteps"]); }
        catch (Exception error) when (error is FormatException or OverflowException) { throw new InvalidDataException("Invalid timesteps.", error); }
        if (steps is not null && (steps.Length < 2 || steps.Length > 201 || steps.Any(v => !double.IsFinite(v) || v < 0 || v > 1) || steps.Zip(steps.Skip(1)).Any(v => v.First <= v.Second)))
            throw new InvalidDataException("timesteps: descending numbers in [0, 1], separated by commas.");
    }
    public static double[]? ParseTimesteps(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Split(',').Select(v => double.Parse(v.Trim(), System.Globalization.CultureInfo.InvariantCulture)).ToArray();
    public static IReadOnlyList<string> Components { get; } = [Variation, Companions, RuntimeId];
    public static IReadOnlyList<ManagedModelArtifactCard> Cards(string root)
    {
        var manifest = JsonSerializer.Deserialize<Files>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Tools", "ace-xl-models.json")))!;
        return [Card(Variation, "ACE-Step/acestep-v15-xl-turbo", ModelRevision, manifest.Model, "ACE-Step 1.5 · XL Turbo · 4B"),
            Card(Companions, "ACE-Step/Ace-Step1.5", CompanionRevision, manifest.Companions, "ACE · VAE + Qwen3-Embedding + LM 1.7B"),
            Card(RuntimeId, "ACE-Step/ACE-Step-1.5", SourceRevision, manifest.Runtime, "ACE · Python libraries")];
        ManagedModelArtifactCard Card(string id, string repo, string rev, List<ManagedModelArtifactFile> files, string name) => new() {
            ModelArtifactId = id, DisplayName = name, Family = ModelName, Role = ManagedModelRoles.Tool,
            Provider = id == RuntimeId ? "PyPI" : "Hugging Face", RepositoryId = repo, Revision = rev,
            Format = id == RuntimeId ? "Python wheels" : "Safetensors", License = id == RuntimeId ? "Upstream package licenses" : "MIT; Qwen Apache-2.0",
            SourcePage = id == RuntimeId ? "https://github.com/ace-step/ACE-Step-1.5/tree/" + SourceRevision : "https://huggingface.co/" + repo,
            RuntimeBackend = "ACE-Step official Python API / PyTorch", IsManaged = true, CanRemoveFiles = true, ModelsRoot = root,
            InstallDirectory = string.IsNullOrWhiteSpace(root) ? "" : Path.Combine(root, "Music", id, rev[..12]),
            Origin = ManagedModelOrigins.PredefinedScenario,
            Consumers = [new() { Id = ScenarioNavigationCatalog.Music, DisplayName = "Музыка / Music", Kind = "scenario" }], Files = files
        };
    }
    private sealed record Files(List<ManagedModelArtifactFile> Model, List<ManagedModelArtifactFile> Companions, List<ManagedModelArtifactFile> Runtime);
}
