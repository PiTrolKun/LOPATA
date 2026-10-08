using System.IO;
using System.Text.Json;

namespace AIHub.Services;

/// <summary>Studio's two stages use the existing durable job/checkpoint runner.</summary>
public sealed class MusicStudioWorker : IMusicYueWorker
{
    public event Action<string>? Log;
    public event Action<MusicHardwareChoice>? HardwareChanged;
    public string? LastHardware { get; private set; }
    public string? LastRuntimePack => MusicStudioRuntime.Pack;
    public string? LastRequestReceipt { get; private set; }
    private readonly HashSet<string> _devices = [];
    public static object Request(MusicYueRequest request, bool plan) => plan ? new {
        style = request.Style, lyrics = request.Lyrics, cot = request.EffectiveExpert.Cot,
        lm_seed = request.LanguageSeed, lm_batch_size = 1,
        abc_sampling = request.EffectiveExpert.Sampling("abc_sampling", Math.Max(1, request.EffectivePlanLimit))
    } : (object)new {
        style = request.Style, lyrics = request.Lyrics, abc = request.Abc, cot = request.EffectiveExpert.Cot,
        lm_seed = request.LanguageSeed, seed = request.SoundSeed, duration_seconds = request.DurationSeconds,
        lm_batch_size = 1, synth_batch_size = 1, steps = request.EffectiveExpert.Integer("steps"),
        cfg_scale = request.EffectiveExpert.Get("cfg_scale"), peak_clip = request.EffectiveExpert.Integer("peak_clip"),
        output_format = "wav16", abc_sampling = request.EffectiveExpert.Sampling("abc_sampling", Math.Max(1, request.EffectivePlanLimit)),
        semantic_sampling = request.EffectiveExpert.Sampling("semantic_sampling", request.SequenceLimit)
    };
    public Task PlanAsync(string model, MusicYueRequest request, string requestPath, string planPath, CancellationToken token) =>
        RunAsync(model, request, requestPath, planPath, true, token);
    public Task SynthesizeAsync(string model, string decoder, MusicYueRequest request, string requestPath, string outputPath, CancellationToken token) =>
        RunAsync(model, request, requestPath, outputPath, false, token);
    private async Task RunAsync(string model, MusicYueRequest request, string requestPath, string output, bool plan, CancellationToken token)
    {
        request.Validate(); LastRequestReceipt = null; _devices.Clear();
        if (request.EffectiveExpert.Variation != MusicStudioRuntime.Variation) throw new InvalidDataException("Studio settings required.");
        if (!plan && request.EffectiveExpert.Cot != "off" && string.IsNullOrWhiteSpace(request.Abc)) throw new InvalidDataException("Studio synthesis needs a saved score.");
        if (File.Exists(output)) throw new IOException("Studio stage output already exists.");
        var modelsRoot = Directory.GetParent(Path.GetDirectoryName(model)!)!.Parent!.Parent!.FullName;
        await ComponentLicenseGate.EnsureAsync(MusicModelVariants.Components(MusicStudioRuntime.Variation), token);
        await MusicStudioRuntime.VerifyAsync(token); await MusicStudioRuntime.VerifyModelsAsync(modelsRoot, token);
        await Task.Run(() => request.CheckContext(new MusicTokenizer(MusicTokenizerMetadata.Read(model, token)), token), token);
        var choice = await MusicHardwareProbe.CheckAsync(MusicYueRuntime.DirectoryPath, model,
            MusicModelVariants.Artifact(modelsRoot, MusicStudioRuntime.Variation, MusicComponentCatalog.DecoderId), request, !plan, token, Emit);
        // The original policy is a preflight estimate. Studio independently validates its device and may fall back.
        HardwareChanged?.Invoke(choice); LastHardware = "Studio auto; preflight=" + choice.Device.Backend + ": " + choice.Device.Description;
        var body = Request(request, plan);
        await using (var file = new FileStream(requestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
            await JsonSerializer.SerializeAsync(file, body, cancellationToken: token); file.Flush(true);
        }
        await using var lease = await MusicStudioLease.StartAsync(modelsRoot, requestPath + ".studio", choice.Demand.ContextTokens, Emit, token);
        var route = plan ? "v1/scores" : "v1/music/jobs";
        var submitted = await lease.SendAsync(route, body, token);
        var id = submitted.GetProperty("id").GetString() ?? throw new InvalidDataException("Studio job ID missing.");
        if (id.Length > 128 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) throw new InvalidDataException("Invalid Studio job ID.");
        var last = "";
        while (true)
        {
            token.ThrowIfCancellationRequested(); await lease.ReadLogsAsync(token);
            var status = await lease.SendAsync(route + "/" + id, null, token);
            var state = status.GetProperty("status").GetString()!.ToLowerInvariant();
            var message = status.TryGetProperty("message", out var detail) ? detail.GetString() : state;
            if (last != message) { Emit("[Studio] job=" + id + "; " + message); last = message ?? ""; }
            if (state is "failed" or "cancelled") throw new InvalidOperationException("Studio " + state + ": " +
                (status.TryGetProperty("error", out var error) ? error.GetString() : message));
            if (state is "done" or "completed")
            {
                if (!plan && status.TryGetProperty("generation_settings", out var effective)) {
                    var receipt = effective.GetRawText();
                    if (receipt.Length > 1_048_576) throw new InvalidDataException("Oversized Studio request receipt.");
                    LastRequestReceipt = receipt;
                }
                await File.WriteAllTextAsync(requestPath + ".result.json", status.GetRawText(), token);
                var temporary = output + "." + Guid.NewGuid().ToString("N") + ".partial";
                try {
                    if (plan) {
                        var abc = status.GetProperty("abc").GetString();
                        if (string.IsNullOrWhiteSpace(abc) || abc.Length > 1_048_576) throw new InvalidDataException("Studio returned an empty/oversized score.");
                        await File.WriteAllTextAsync(temporary, abc, token);
                    } else {
                        var song = status.GetProperty("song");
                        await lease.CopyAudioAsync(song.GetProperty("audio_url").GetString()!, temporary, token);
                        _ = MusicWaveFile.ReadDuration(temporary);
                    }
                    File.Move(temporary, output, false);
                } finally { if (File.Exists(temporary)) File.Delete(temporary); }
                await lease.ReadLogsAsync(token); return;
            }
            await Task.Delay(500, token);
        }
        // Cancellation disposes the owned process tree before the runner confirms pause.
    }
    private void Emit(string text)
    {
        if (text.StartsWith("[Load]", StringComparison.Ordinal) && text.Contains("backend:", StringComparison.Ordinal)
            && _devices.Add(text)) LastHardware += "; actual=" + text;
        try { Log?.Invoke(text[..Math.Min(text.Length, 4096)]); }
        catch (Exception error) { OwnedProcessRegistry.Log("music_log_callback_failed", "Music.Studio", detail: error.GetType().Name); }
    }
}
