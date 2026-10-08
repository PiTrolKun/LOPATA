using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace AIHub.Services;

/// <summary>One cancellable offline process per durable stage; never installs libraries or weights.</summary>
public sealed class MusicBf16Worker : IMusicYueWorker
{
    public event Action<string>? Log;
    public string? LastHardware { get; private set; }
    public string? LastRuntimePack { get; private set; }
    public Task PlanAsync(string model, MusicYueRequest request, string requestPath, string planPath, CancellationToken token) =>
        RunAsync(model, DecoderBeside(model), request, requestPath, planPath, "plan", token);
    public Task SynthesizeAsync(string model, string decoder, MusicYueRequest request, string requestPath, string outputPath, CancellationToken token) =>
        RunAsync(model, decoder, request, requestPath, outputPath, "synth", token);
    private static string Root(string model) => Directory.GetParent(Path.GetDirectoryName(model)!)!.Parent!.Parent!.FullName;
    private static string DecoderBeside(string model) => MusicModelVariants.Artifact(Root(model), MusicModelVariants.Bf16, MusicModelVariants.Bf16Vae);
    internal static async Task<string> ProbeAsync(string root, CancellationToken token, Action<string> log)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var worker = new MusicBf16Worker(); worker.Log += log;
        var path = Path.Combine(Path.GetTempPath(), "lopata-bf16-" + Guid.NewGuid().ToString("N"));
        try { await worker.RunAsync(MusicModelVariants.Artifact(root, MusicModelVariants.Bf16, MusicModelVariants.Bf16),
            MusicModelVariants.Artifact(root, MusicModelVariants.Bf16, MusicModelVariants.Bf16Vae),
            new("", "", 1, 1, 1) { Expert = MusicModelVariants.Defaults(MusicModelVariants.Bf16) }, path + ".json", path + ".probe", "probe", timeout.Token);
            return worker.LastHardware ?? "BF16"; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new IOException("YuE2 BF16 hardware check timed out after 60 seconds."); }
        finally { if (File.Exists(path + ".json")) File.Delete(path + ".json"); if (File.Exists(path + ".probe")) File.Delete(path + ".probe"); }
    }
    internal static object Request(MusicYueRequest request) => new {
        style = request.Style, lyrics = request.Lyrics, seed = request.SoundSeed, cot = request.EffectiveExpert.Cot,
        cfg_scale = request.EffectiveExpert.Get("cfg_scale") < 0 ? (double?)null : request.EffectiveExpert.Get("cfg_scale"),
        steps = request.EffectiveExpert.Integer("steps"), abc = request.Abc,
        abc_sampling = request.EffectiveExpert.Sampling("abc_sampling", Math.Max(1, request.EffectivePlanLimit)),
        semantic_sampling = request.EffectiveExpert.Sampling("semantic_sampling", request.SequenceLimit)
    };
    private async Task RunAsync(string model, string decoder, MusicYueRequest request, string requestPath, string outputPath,
        string stage, CancellationToken token)
    {
        request.Validate(); if (request.EffectiveExpert.Variation != MusicModelVariants.Bf16) throw new InvalidDataException("BF16 settings required.");
        if (File.Exists(outputPath)) throw new IOException("Music output already exists.");
        Log?.Invoke("[Load] YuE2 BF16 · " + stage);
        var overlay = await MusicBf16Runtime.PrepareAsync(Root(model), token);
        var runtime = await ManagedPythonRuntime.ResolveAsync("auto", token);
        LastRuntimePack = runtime.Entry.Id + "/" + MusicModelVariants.RuntimeRevision;
        // Stage checkpoint contains exact token IDs, not only an ABC decode/encode round trip.
        var budgetRequest = request;
        if (stage == "synth" && request.EffectiveExpert.Cot != "off") {
            using var plan = JsonDocument.Parse(request.Abc);
            budgetRequest = request with { Abc = plan.RootElement.GetProperty("abc").GetString()! };
        }
        await Task.Run(() => budgetRequest.CheckContext(new MusicBf16Tokenizer(Path.Combine(Path.GetDirectoryName(model)!, "qwen.tiktoken"), token), token), token);
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(Request(request)), token);
        var temporary = outputPath + "." + Guid.NewGuid().ToString("N") + ".partial";
        var info = new ProcessStartInfo(runtime.Python) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8 };
        foreach (var key in new[] { "HF_HUB_OFFLINE", "TRANSFORMERS_OFFLINE" }) info.Environment[key] = "1";
        info.Environment["PYTHONNOUSERSITE"] = "1";
        ManagedPythonLaunch.ScriptArguments(info, [Path.Combine(AppContext.BaseDirectory, "Tools", "music_bf16_worker.py"),
            overlay, stage, Path.GetDirectoryName(model)!, Path.GetDirectoryName(decoder)!, requestPath, temporary, runtime.Device]);
        token.ThrowIfCancellationRequested();
        using var process = OwnedProcessRegistry.Shared.Start(info, "Music.YuE2.BF16");
        var errors = new Queue<string>();
        async Task Pump(StreamReader reader) {
            string? line; while ((line = await reader.ReadLineAsync()) is not null) {
                if (line.Length > 4096) line = line[..4096];
                if (line.StartsWith("[Hardware]", StringComparison.Ordinal)) LastHardware = line[10..].Trim();
                lock (errors) { errors.Enqueue(line); while (errors.Count > 12) errors.Dequeue(); }
                try { Log?.Invoke(line); }
                catch (Exception callback) { OwnedProcessRegistry.Log("music_log_callback_failed", "Music.YuE2.BF16", detail: callback.GetType().Name); }
            }
        }
        var stdout = Pump(process.StandardOutput); var stderr = Pump(process.StandardError);
        try {
            await process.WaitForExitAsync(token);
            await Task.WhenAll(stdout, stderr);
            token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new IOException("YuE2 BF16 worker failed (" + process.ExitCode + "). " + string.Join("\n", errors));
            if (stage == "synth") _ = MusicWaveFile.ReadDuration(temporary);
            else { if (new FileInfo(temporary).Length > 1_048_576) throw new InvalidDataException("Oversized BF16 plan."); using var plan = JsonDocument.Parse(await File.ReadAllTextAsync(temporary, token)); }
            File.Move(temporary, outputPath, false);
        }
        finally {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); }
            await Task.WhenAll(stdout, stderr);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
