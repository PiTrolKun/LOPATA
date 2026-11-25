using System.Text.Json;

namespace AIHub.Services.Shorts;

internal enum ShortsStage { Prepare, Generate, Upscale, Save }

internal sealed record ShortsProgress(ShortsStage Stage = ShortsStage.Prepare, string Detail = "Shorts.Preparing",
    int Fragment = 0, int Total = 0, int Step = 0, int Steps = 0, bool EstimatedTotal = false)
{
    internal ShortsProgress Update(string message)
    {
        if (message == "Shorts.Preparing") return new();
        if (message is "Shorts.Upscaling" or "Shorts.Dlss" or "Shorts.SoftwareScale")
            return new(ShortsStage.Upscale, message);
        if (message == "Shorts.Exporting") return new(ShortsStage.Save, message);
        if (message == "SongClip.DenoFinishing") return new(ShortsStage.Upscale, message);
        if (message == "SongClip.PlanReady") return new(ShortsStage.Prepare, message);
        if (message == "SongClip.Finalizing") return new(ShortsStage.Generate, "SongClip.MotionVerify");
        const string h3Prefix = "[H3 event] ";
        var h3 = message.StartsWith(h3Prefix, StringComparison.Ordinal);
        if (!h3 && !message.StartsWith('{')) return this;
        try
        {
            using var json = JsonDocument.Parse(h3 ? message[h3Prefix.Length..] : message);
            var value = json.RootElement;
            if (value.ValueKind != JsonValueKind.Object) return this;
            string? String(string key) => value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            int Number(string key) => value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.Number
                && item.TryGetInt32(out var number) ? Math.Max(0, number) : 0;
            var index = Number("index");
            var total = Number("total");
            return String("kind") switch
            {
                "pass" or "chunk-start" when index > 0 =>
                    new(ShortsStage.Generate, "Shorts.Generating", index, total, EstimatedTotal: h3),
                "progress" when h3 && Detail == "Shorts.Generating" && Fragment > 0 && Number("total") > 0 =>
                    this with { Step = Math.Min(Number("value"), Number("total")), Steps = Number("total") },
                "progress" when !h3 && index > 0 && Number("steps") > 0 =>
                    new(ShortsStage.Generate, "Shorts.Generating", index, total,
                        Math.Min(Number("step"), Number("steps")), Number("steps")),
                "chunk" or "cached" when !h3 && index > 0 =>
                    new(ShortsStage.Generate, "Shorts.FragmentReady", index, total, 1, 1),
                "analysis" or "planner" when !h3 => new(ShortsStage.Prepare, "Shorts.Planning"),
                "stage" => UpdateEngineStage(h3, String(h3 ? "text" : "stage")),
                _ => this
            };
        }
        catch (JsonException) { return this; }
    }

    private ShortsProgress UpdateEngineStage(bool h3, string? stage) => stage switch
    {
        "montage-analysis" => new(ShortsStage.Prepare, "SongClip.MontageAnalysis"),
        "montage-assembly" or "assembling" => new(ShortsStage.Generate, "Shorts.Assembling"),
        "deno-finish" => new(ShortsStage.Upscale, "SongClip.DenoFinishing"),
        "decoding" when h3 => this with { Detail = "VideoPrep.H3Stage.decoding", Step = 0, Steps = 0 },
        "sampling" or "render" => this with { Stage = ShortsStage.Generate, Detail = "Shorts.Generating" },
        "conditioning" or "loading-text-encoder" or "loading-vae" or "loading-diffusion" or "connecting-plans" when h3 =>
            this with { Detail = "VideoPrep.H3Stage." + stage },
        _ => this
    };
}
