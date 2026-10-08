using System.IO;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Explicit fields have priority; ambiguous wishes stay in caption and are reported.</summary>
public static class MusicAceRequest
{
    public static Dictionary<string, object?> Build(MusicYueRequest request)
    {
        var expert = request.EffectiveExpert; expert.Validate();
        if (!MusicAceCatalog.IsAce(expert.Variation)) throw new InvalidDataException("ACE settings required.");
        var warnings = new List<string>();
        var wishes = request.Wishes;
        var instrumental = wishes?.Instrumental == true;
        var language = expert.TextValues["vocal_language"];
        if (language == "auto") {
            var languages = wishes?.Performers.Select(p => Language(p.Language)).Where(v => v != "unknown").Distinct().ToArray() ?? [];
            language = languages.Length == 1 ? languages[0] : "unknown";
            if (languages.Length > 1) warnings.Add("Multiple vocal languages: preserved in caption; no single vocal_language forced.");
        }
        var meter = expert.TextValues["timesignature"];
        if (meter == "auto") {
            var rhythms = wishes?.Selections.GetValueOrDefault("rhythm") ?? [];
            var meters = rhythms.Select(v => v switch { "waltz" => "3", "four-four" => "4", "six-eight" => "6", _ => "" }).Where(v => v.Length > 0).Distinct().ToArray();
            meter = meters.Length == 1 && !rhythms.Contains("twelve-eight") && !rhythms.Contains("free") ? meters[0] : "";
            if (meters.Length > 1 || rhythms.Contains("twelve-eight") || rhythms.Contains("free")) warnings.Add("Ambiguous or unsupported time signature: preserved in caption; timesignature automatic.");
        }
        var negative = expert.TextValues["lm_negative_prompt"];
        if (negative.Length == 0 && wishes is not null)
            negative = string.Join(", ", (wishes.Selections.GetValueOrDefault("avoid") ?? []).Select(id => MusicWishCatalog.Prompt("avoid", id)));
        if (negative.Length == 0) negative = "NO USER INPUT";
        var result = new Dictionary<string, object?> {
            ["task_type"] = "text2music", ["caption"] = request.Style,
            ["lyrics"] = instrumental ? "[Instrumental]" : request.Lyrics, ["instrumental"] = instrumental,
            ["vocal_language"] = instrumental ? "unknown" : language,
            ["bpm"] = expert.Integer("bpm") == 0 ? null : expert.Integer("bpm"),
            ["timesignature"] = meter, ["keyscale"] = expert.TextValues["keyscale"],
            ["duration"] = request.DurationAutomatic ? -1 : request.DurationSeconds,
            ["seed"] = request.SoundSeed, ["retake_seed"] = expert.Integer("retake_seed") < 0 ? null : expert.Integer("retake_seed"),
            ["infer_method"] = new[] { "ode", "sde" }[expert.Integer("infer_method")],
            ["sampler_mode"] = new[] { "euler", "heun" }[expert.Integer("sampler_mode")],
            ["dcw_enabled"] = expert.Integer("dcw_enabled") < 0 ? null : expert.Integer("dcw_enabled") == 1,
            ["dcw_mode"] = expert.TextValues["dcw_mode"], ["dcw_wavelet"] = expert.TextValues["dcw_wavelet"],
            ["lm_negative_prompt"] = negative, ["use_cot_lyrics"] = false,
            ["timesteps"] = MusicAceCatalog.ParseTimesteps(expert.TextValues["timesteps"]),
            // XL Turbo does not implement Base classifier-free guidance or ADG.
            ["guidance_scale"] = 1.0, ["use_adg"] = false, ["cfg_interval_start"] = 0.0, ["cfg_interval_end"] = 1.0
        };
        foreach (var p in MusicAceCatalog.Parameters.Where(p => !result.ContainsKey(p.Key) && p.Group != "Runtime"))
            result[p.Key] = p.Maximum == 1 && p.Minimum == 0 && p.Step == 1 ? (object)(expert.Get(p.Key) == 1)
                : p.Step == 1 ? (object)expert.Integer(p.Key) : expert.Get(p.Key);
        return new() { ["params"] = result, ["runtime"] = MusicAceCatalog.Parameters.Where(p => p.Group == "Runtime").ToDictionary(p => p.Key, p => expert.Get(p.Key)),
            ["config"] = new { batch_size = 1, allow_lm_batch = false, use_random_seed = false, seeds = new[] { request.SoundSeed },
                audio_format = "wav", constrained_decoding_debug = expert.Get("constrained_decoding_debug") == 1 },
            ["warnings"] = warnings.ToArray(), ["source_revision"] = MusicAceCatalog.SourceRevision,
            ["model_revision"] = MusicAceCatalog.ModelRevision, ["companion_revision"] = MusicAceCatalog.CompanionRevision };
    }
    private static string Language(string value) => value switch {
        "russian" => "ru", "english" => "en", "chinese" => "zh", "japanese" => "ja", "korean" => "ko",
        "french" => "fr", "german" => "de", "spanish" => "es", "italian" => "it", "portuguese" => "pt", _ => "unknown" };
}
