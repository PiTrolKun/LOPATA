using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIHub.Services.MusicLora;

/// <summary>Conservative signatures for formats already understood by the pinned native executors.</summary>
internal static class MusicLoraTensorRules
{
    internal static void ValidateStorage(JsonElement root, long bytes)
    {
        var names = root.EnumerateObject().Select(p => p.Name).ToArray();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length) Fail();
        var ranges = new List<(long Start, long End)>();
        foreach (var p in root.EnumerateObject()) {
            if (p.Name == "__metadata__") continue;
            var dtype = p.Value.GetProperty("dtype").GetString(); var width = dtype is "F32" ? 4 : dtype is "F16" or "BF16" ? 2 : 0;
            var shape = p.Value.GetProperty("shape").EnumerateArray().Select(n => n.GetInt64()).ToArray();
            var offsets = p.Value.GetProperty("data_offsets").EnumerateArray().Select(n => n.GetInt64()).ToArray();
            if (width == 0 || shape.Length > 2 || shape.Any(n => n <= 0 || n > 1000000) || offsets.Length != 2
                || offsets[0] < 0 || offsets[1] <= offsets[0] || offsets[1] > bytes
                || shape.Aggregate((long)width, (a, b) => checked(a * b)) != offsets[1] - offsets[0]) Fail();
            ranges.Add((offsets[0], offsets[1]));
        }
        if (ranges.Count is < 2 or > 5000) Fail();
        long end = 0; foreach (var range in ranges.OrderBy(r => r.Start)) { if (range.Start != end) Fail(); end = range.End; }
        if (end != bytes) Fail();
    }
    internal static string Meta(JsonElement header, string key) => header.TryGetProperty("__metadata__", out var m)
        && m.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
    private static void Fail() => throw new InvalidDataException("Music.Lora.Search.TensorMismatch");
    private static void Pair(JsonElement root, string a, string b, int input, int output, int? rank = null)
    {
        if (!root.TryGetProperty(a, out var left)) Fail();
        if (!root.TryGetProperty(b, out var right)) Fail();
        var x = left.GetProperty("shape").EnumerateArray().Select(n => n.GetInt32()).ToArray();
        var y = right.GetProperty("shape").EnumerateArray().Select(n => n.GetInt32()).ToArray();
        if (x.Length != 2 || y.Length != 2 || x[0] is < 1 or > 128 || (rank.HasValue && x[0] != rank)
            || x[1] != input || y[0] != output || y[1] != x[0] || left.GetProperty("dtype").GetString() != right.GetProperty("dtype").GetString()) Fail();
    }
    private static (int Input, int Output) Shape(string module, bool ace)
    {
        var hidden = ace ? 2560 : 2048; var mlp = ace ? 9728 : 6144; var q = ace ? 4096 : 2048;
        if (module.EndsWith("down_proj")) return (mlp, hidden);
        if (module.EndsWith("gate_up_proj")) return (hidden, 2 * mlp);
        if (module.Contains("mlp.")) return (hidden, mlp);
        if (module.EndsWith("o_proj") || module.EndsWith("to_out.0")) return (q, hidden);
        if (module.EndsWith("qkv_proj")) return (hidden, q + 2048);
        if (module.EndsWith("q_proj") || module.EndsWith("to_q")) return (hidden, q);
        return (hidden, 1024);
    }
    internal static void Ace(JsonElement h, bool slider, int rank)
    {
        var pattern = slider ? @"\Alayers\.(\d+)\.((?:self_attn|cross_attn)\.(?:to_q|to_k|to_v|to_out\.0)|mlp\.(?:gate_proj|up_proj|down_proj))\.down\z"
            : @"\Abase_model\.model\.layers\.(\d+)\.((?:self_attn|cross_attn)\.(?:q_proj|k_proj|v_proj|o_proj)|mlp\.(?:gate_proj|up_proj|down_proj))\.lora_A\.weight\z";
        var consumed = new HashSet<string>(); var modules = new HashSet<string>();
        foreach (var p in h.EnumerateObject()) {
            var match = Regex.Match(p.Name, pattern); if (!match.Success) continue;
            if (int.Parse(match.Groups[1].Value) >= 32) Fail();
            var b = slider ? p.Name[..^5] + ".up" : p.Name.Replace(".lora_A.weight", ".lora_B.weight");
            var shape = Shape(match.Groups[2].Value, true); Pair(h, p.Name, b, shape.Input, shape.Output, rank);
            if (slider && p.Value.GetProperty("dtype").GetString() != "F32") Fail();
            consumed.Add(p.Name); consumed.Add(b); modules.Add("layers." + match.Groups[1].Value + "." + match.Groups[2].Value);
        }
        if (consumed.Count == 0 || h.EnumerateObject().Any(p => p.Name != "__metadata__" && !consumed.Contains(p.Name))) Fail();
        // The existing converter accepts only the all-targets slider recipe.
        if (slider && modules.Count != 32 * 11) Fail();
    }
    internal static void AceConfiguration(JsonElement h, JsonElement config)
    {
        if (!config.TryGetProperty("target_modules", out var targets) || targets.ValueKind != JsonValueKind.Array) Fail();
        var allowed = targets.EnumerateArray().Select(v => v.GetString() ?? "").ToArray();
        if (allowed.Length is < 1 or > 352 || allowed.Distinct().Count() != allowed.Length) Fail();
        var modules = Enumerable.Range(0, 32).SelectMany(layer => new[] {
            "self_attn.q_proj", "self_attn.k_proj", "self_attn.v_proj", "self_attn.o_proj", "cross_attn.q_proj", "cross_attn.k_proj",
            "cross_attn.v_proj", "cross_attn.o_proj", "mlp.gate_proj", "mlp.up_proj", "mlp.down_proj" }.Select(m => "layers." + layer + "." + m)).ToArray();
        bool Matches(string module, string target) => target.Length > 0 && (module == target || module.EndsWith("." + target, StringComparison.Ordinal));
        if (allowed.Any(t => !modules.Any(m => Matches(m, t)))) Fail();
        var expected = modules.Where(m => allowed.Any(t => Matches(m, t)))
            .SelectMany(m => new[] { "base_model.model." + m + ".lora_A.weight", "base_model.model." + m + ".lora_B.weight" }).ToHashSet();
        if (!expected.SetEquals(h.EnumerateObject().Where(p => p.Name != "__metadata__").Select(p => p.Name))) Fail();
    }
    internal static Dictionary<string, double> Studio(JsonElement h, bool exactBase = false)
    {
        var meta = Meta(h, "particle_lora");
        if (meta.Length > 0) {
            using var particle = JsonDocument.Parse(meta); var p = particle.RootElement;
            if (!p.GetProperty("ordinary_lora").GetBoolean() || p.GetProperty("format").GetString() != "conceptmod-yue2-ar-v1"
                || p.GetProperty("base_model").GetProperty("model_id").GetString() != "m-a-p/YuE2-3B"
                || !p.GetProperty("inference_stages").EnumerateArray().Select(v => v.GetString()).SequenceEqual(["semantic"])) Fail();
            var used = new HashSet<string>(); var rank = p.GetProperty("rank").GetInt32();
            foreach (var t in h.EnumerateObject()) {
                var m = Regex.Match(t.Name, @"\Aadapters\.model-layers-(\d+)-self_attn-(q_proj|k_proj|v_proj|o_proj)\.lora_down\.weight\z");
                if (!m.Success) continue; if (int.Parse(m.Groups[1].Value) >= 28) Fail();
                var stem = t.Name[..^17]; var b = stem + ".lora_up.weight"; var alpha = stem + ".alpha";
                var shape = Shape("self_attn." + m.Groups[2].Value, false); Pair(h, t.Name, b, shape.Input, shape.Output, rank);
                if (!h.TryGetProperty(alpha, out var a) || a.GetProperty("shape").GetArrayLength() != 0) Fail();
                used.UnionWith([t.Name, b, alpha]);
            }
            if (used.Count == 0 || h.EnumerateObject().Any(t => t.Name != "__metadata__" && !used.Contains(t.Name))) Fail();
            return new() { ["ar"] = 1 };
        }
        // Bundled native v9 has explicit base checkpoint and companion identity.
        var bundle = Meta(h, "format") == "sound-vision-yue2-v9-bundle-v1"
            && Meta(h, "base_revision") == "1a96eca688d6ae5d7f0feb88573fec89920fcd19"
            && Meta(h, "sound_vision_companion") == "yue2-nar-v9";
        var native = Meta(h, "base_model") == "m-a-p/YuE2-3B" && Meta(h, "lora_scale") == "1.0";
        // These published native conversions already fold scale into B, or preserve
        // the trainer's alpha metadata handled by the pinned native loader.
        var branch = Meta(h, "branch");
        var converted = exactBase && branch is "ar" or "nar" &&
            (Meta(h, "converted_from") == "ai-toolkit fused YuE2 LoRA"
             || Meta(h, "protocol") == "yue2-native-v1"
                && Meta(h, "rule") == "W += lora_B @ lora_A (scale folded into lora_B)");
        var comfy = exactBase && h.EnumerateObject().Any(t => t.Name.StartsWith("text_encoders.model.layers.", StringComparison.Ordinal))
            && h.EnumerateObject().Any(t => t.Name.StartsWith("diffusion_model.model.layers.", StringComparison.Ordinal));
        if (!bundle && !native && !converted && !comfy) throw new InvalidDataException("Music.Lora.Search.BaseUnconfirmed");
        var consumed = new HashSet<string>(); var slots = new HashSet<string>();
        foreach (var t in h.EnumerateObject()) {
            var name = t.Name; var comfyNar = name.StartsWith("diffusion_model.model.", StringComparison.Ordinal);
            var comfyAr = name.StartsWith("text_encoders.model.", StringComparison.Ordinal);
            if (comfy && (comfyNar || comfyAr)) name = name[(comfyNar ? "diffusion_model.model.".Length : "text_encoders.model.".Length)..];
            else if (comfy) continue;
            var m = Regex.Match(name, @"\A(?:(companion)\.)?(?:model\.)?layers\.(\d+)\.((?:(?:nar_)?self_attn\.(?:q_proj|k_proj|v_proj|o_proj|qkv_proj))|(?:(?:nar_)?mlp\.(?:gate_proj|up_proj|down_proj|gate_up_proj)))\.lora_A(?:\.weight)?\z");
            if (!m.Success) continue; if (int.Parse(m.Groups[2].Value) >= 28) Fail();
            var b = t.Name.Replace(".lora_A", ".lora_B"); var s = Shape(m.Groups[3].Value, false);
            Pair(h, t.Name, b, s.Input, s.Output); consumed.UnionWith([t.Name, b]);
            slots.Add(comfyNar || m.Groups[1].Success || m.Groups[3].Value.StartsWith("nar_") ? "nar" : "ar");
        }
        if (consumed.Count == 0 || h.EnumerateObject().Any(t => t.Name != "__metadata__" && !consumed.Contains(t.Name))
            || (bundle || comfy) && !slots.SetEquals(["ar", "nar"]) || native && !slots.SetEquals(["ar"])
            || converted && !slots.SetEquals([branch])) Fail();
        return slots.ToDictionary(s => s, _ => 1d);
    }
}
