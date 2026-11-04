using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIHub.Services.VideoUpscale;

internal sealed record UpscaleParameter(string Key, string Default, double? Min = null, double? Max = null, string[]? Choices = null)
{
    public string NameKey => "VideoUpscale.Param." + Key;
    public string HelpKey => NameKey + ".Help";
}

internal static class UpscaleCatalog
{
    internal static readonly string[] Algorithms = ["lanczos", "bicubic", "spline", "bilinear", "neighbor", "area"];
    internal static readonly UpscaleParameter[] FlashParameters = [
        new("mode", "turbo", Choices: ["turbo", "base", "v1"]),
        new("precision", "int8_convrot", Choices: ["int8_convrot", "bf16", "fp16"]),
        new("device", "auto"), new("seed", "1", 0, int.MaxValue),
        new("decode_every", "4", 4, 256), new("temporal_compression", "4", Choices: ["1", "2", "4"]),
        new("temporal_merge", "gaussian", Choices: ["gaussian", "first"]), new("temporal_gaussian_sigma", "1", .01, 10),
        new("cache_offload_mode", "auto", Choices: ["auto", "always", "never"]), new("offload_original_cache", "false", Choices: ["false", "true"]),
        new("tiny_decoder_mode", "serial", Choices: ["auto", "serial", "parallel"]),
        new("tiny_decoder_channels_last", "true", Choices: ["true", "false"]),
        new("color_correction", "true", Choices: ["true", "false"]), new("color_correction_levels", "5", 1, 8),
        new("color_correction_chunk_size", "16", 1, 64), new("ffn_chunk_tokens", "8192", 0, 262144)
    ];
    internal static readonly UpscaleParameter[] NrParameters = [
        new("nr_style", "Style 0", Choices: ["Style 0", "Style 1", "Style 2"]), new("nr_intensity", "1", 0, 2),
        new("nr_passes", "1", 1, 4), new("automatic_mask", "true", Choices: ["true", "false"]),
        new("local_tone_strength", "0", 0, 2), new("local_structure_strength", "0", 0, 2), new("skin_structure_strength", "0", 0, 2),
        new("nr_optical_flow_quality", "High", Choices: ["Low", "Medium", "High"]), new("gpu", "auto")
    ];
    internal static UpscaleParameter[] Parameters(UpscaleEngine engine) => engine switch {
        UpscaleEngine.Flash => FlashParameters,
        UpscaleEngine.NeuralRendering => NrParameters,
        UpscaleEngine.RtxVideo => [new("upscale_vsr_quality", "4", 1, 4), new("gpu", "auto")],
        UpscaleEngine.DlssSr => [new("upscale_dlss_mode", "Quality", Choices: ["DLAA", "Quality", "Balanced", "Performance", "Ultra Performance"]), new("gpu", "auto")],
        UpscaleEngine.VfxVsr => [new("vfx_vsr_mode", "3", 0, 3), new("vfx_vsr_strength", "1", 0, 1), new("gpu", "auto")],
        UpscaleEngine.VfxUpscale => [new("vfx_upscale_strength", "0.4", 0, 1), new("gpu", "auto")],
        _ => [new("sharpen", "0", 0, 2), new("denoise", "0", 0, 10)]
    };
    internal static List<UpscaleStep> Preset(UpscaleMode mode) => mode switch {
        UpscaleMode.Manual => [New(UpscaleEngine.Scale, 3840, 2160)],
        UpscaleMode.Heavy => [New(UpscaleEngine.Flash, 1920, 1080), New(UpscaleEngine.Scale, 2560, 1440),
            new() { Engine = UpscaleEngine.Flash, Width = 2560, Height = 1440, Mix = .25 }, New(UpscaleEngine.Scale, 3840, 2160)],
        _ => [New(UpscaleEngine.Flash, 1920, 1080), New(UpscaleEngine.Scale, 3840, 2160)]
    };
    internal static UpscaleStep New(UpscaleEngine engine, int width = 3840, int height = 2160) => new() { Engine = engine, Width = width, Height = height };
    internal static double Number(UpscaleStep step, string key, string fallback) => double.Parse(step.Get(key, fallback), CultureInfo.InvariantCulture);
    internal static string Fingerprint(UpscaleProject p) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new { Pipeline = "encoded-parts-2", p.SourceSha256, p.Steps, p.Codec, p.BitrateMbps, p.CpuThreads, p.ChunkSeconds }))));
    internal static void Validate(UpscaleProject p, bool allowEmptyChain = false)
    {
        if (!Guid.TryParseExact(p.Id, "N", out _) || p.Schema != 1 || p.Media is null || p.Steps is null || p.Steps.Count > 64 || p.Media.Fps is <= 0 or > 1000 || !double.IsFinite(p.Media.Fps)
            || !double.IsFinite(p.PreviewStart) || !double.IsFinite(p.PreviewLength) || p.PreviewStart < 0 || p.PreviewLength is <= 0 or > 5
            || !System.Text.RegularExpressions.Regex.IsMatch(p.Media.Rate, "^[0-9]{1,8}/[1-9][0-9]{0,7}$"))
            throw new InvalidDataException("VideoUpscale.InvalidProject");
        if (!allowEmptyChain && (p.Steps.Count == 0 || !p.Steps.Any(s => s.Enabled))) throw new InvalidOperationException("VideoUpscale.EmptyChain");
        if (p.Steps.Select(s => s.Id).Distinct().Count() != p.Steps.Count) throw new InvalidDataException("VideoUpscale.InvalidProject");
        foreach (var step in p.Steps) {
            if (!Guid.TryParseExact(step.Id, "N", out _) || !Enum.IsDefined(step.Engine) || step.Width is < 128 or > 16384 || step.Height is < 128 or > 16384
                || step.Width % 2 != 0 || step.Height % 2 != 0 || !double.IsFinite(step.Mix) || step.Mix is < 0 or > 1 || !Algorithms.Contains(step.Algorithm))
                throw new InvalidOperationException("VideoUpscale.InvalidParameter");
            var fields = Parameters(step.Engine);
            if (step.Parameters.Keys.Any(k => fields.All(f => f.Key != k))) throw new InvalidOperationException("VideoUpscale.InvalidParameter");
            foreach (var f in fields) {
                var value = step.Get(f.Key, f.Default);
                if (f.Choices is not null && !f.Choices.Contains(value)) throw new InvalidOperationException("VideoUpscale.InvalidParameter");
                if (f.Min is not null && (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || !double.IsFinite(n) || n < f.Min || n > f.Max))
                    throw new InvalidOperationException("VideoUpscale.InvalidParameter");
                if (f.Key is "seed" or "decode_every" or "ffn_chunk_tokens" or "color_correction_levels" or "color_correction_chunk_size" or "nr_passes" or "upscale_vsr_quality" or "vfx_vsr_mode"
                    && !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _)) throw new InvalidOperationException("VideoUpscale.InvalidParameter");
                if (f.Key == "device" && value != "auto" && !System.Text.RegularExpressions.Regex.IsMatch(value, "^cuda:[0-9]{1,2}$")) throw new InvalidOperationException("VideoUpscale.InvalidParameter");
                if (f.Key == "gpu" && value != "auto" && !System.Text.RegularExpressions.Regex.IsMatch(value, "^([0-9]{1,2}|GPU-[A-Fa-f0-9-]{36})$")) throw new InvalidOperationException("VideoUpscale.InvalidParameter");
            }
            if (step.Engine == UpscaleEngine.Flash && Number(step, "decode_every", "4") % 4 != 0) throw new InvalidOperationException("VideoUpscale.DecodeMultiple");
        }
        if (p.Codec is not ("libopenh264" or "hevc_nvenc" or "ffv1") || p.BitrateMbps is < 1 or > 200 || p.CpuThreads is < 0 or > 256
            || !double.IsFinite(p.ChunkSeconds) || p.ChunkSeconds is < .5 or > 5) throw new InvalidOperationException("VideoUpscale.InvalidParameter");
    }
    internal static (int Width, int Height) Fit(int sw, int sh, int width, int height)
    {
        var factor = Math.Min(width / (double)sw, height / (double)sh);
        return (Math.Max(2, (int)Math.Round(sw * factor / 2) * 2), Math.Max(2, (int)Math.Round(sh * factor / 2) * 2));
    }
}
