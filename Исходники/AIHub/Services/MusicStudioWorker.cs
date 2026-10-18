using System.IO;
using System.Text.Json;

namespace AIHub.Services;

/// <summary>Studio's two stages use the existing durable job/checkpoint runner.</summary>
public sealed class MusicStudioWorker : IMusicYueWorker
{
    public event Action<string>? Log;
    public event Action<MusicHardwareChoice>? HardwareChanged;
    public string? LastHardware { get; private set; }
    public string? LastRuntimePack { get; private set; }
    public string? LastRequestReceipt { get; private set; }
    private readonly HashSet<string> _devices = [];
    public static object Request(MusicYueRequest request, bool plan, MusicStudioProfile? profile = null)
    {
        profile ??= Profile(request);
        if (request.EffectiveExpert.StudioSolver == "ab2" && !profile.SupportsAb2)
            throw new InvalidDataException("AB2 requires Studio engine set 002 or later.");
        var body = new Dictionary<string, object> {
            ["style"] = request.Style, ["lyrics"] = request.Lyrics, ["cot"] = request.EffectiveExpert.Cot,
            ["lm_seed"] = request.LanguageSeed, ["lm_batch_size"] = 1,
            ["abc_sampling"] = request.EffectiveExpert.Sampling("abc_sampling", Math.Max(1, request.EffectivePlanLimit)) };
        if (plan) return body;
        body["abc"] = request.Abc; body["seed"] = request.SoundSeed; body["duration_seconds"] = request.DurationSeconds;
        body["synth_batch_size"] = 1; body["steps"] = request.EffectiveExpert.Integer("steps");
        body["cfg_scale"] = request.EffectiveExpert.Get("cfg_scale");
        body["semantic_sampling"] = request.EffectiveExpert.Sampling("semantic_sampling", request.SequenceLimit);
        body["output_format"] = profile.SeparateNormalization ? "wav32" : "wav16";
        if (profile.SeparateNormalization) {
            body["solver"] = request.EffectiveExpert.StudioSolver;
            body["companion_scale"] = 1; body["lyric_timing"] = false;
        } else body["peak_clip"] = request.EffectiveExpert.Integer("peak_clip");
        return body;
    }
    private static MusicStudioProfile Profile(MusicYueRequest request) => request.StudioRuntimeRevision is { } revision
        ? MusicStudioRuntime.ResolveProfile(revision, request.StudioRuntimePack ?? throw new InvalidDataException("Missing Studio pack snapshot."))
        : request.StudioRuntimePack is null ? MusicStudioRuntime.SelectedProfile : throw new InvalidDataException("Missing Studio revision snapshot.");
    public Task PlanAsync(string model, MusicYueRequest request, string requestPath, string planPath, CancellationToken token) =>
        RunAsync(model, request, requestPath, planPath, true, token);
    public Task SynthesizeAsync(string model, string decoder, MusicYueRequest request, string requestPath, string outputPath, CancellationToken token) =>
        RunAsync(model, request, requestPath, outputPath, false, token);
    private async Task RunAsync(string model, MusicYueRequest request, string requestPath, string output, bool plan, CancellationToken token)
    {
        request.Validate(); LastRequestReceipt = null; LastRuntimePack = null; _devices.Clear();
        var profile = Profile(request);
        var body = Request(request, plan, profile); // Reject unsupported settings before loading models.
        if (request.EffectiveExpert.Variation != MusicStudioRuntime.Variation) throw new InvalidDataException("Studio settings required.");
        if (!plan && request.EffectiveExpert.Cot != "off" && string.IsNullOrWhiteSpace(request.Abc)) throw new InvalidDataException("Studio synthesis needs a saved score.");
        if (File.Exists(output)) throw new IOException("Studio stage output already exists.");
        var modelsRoot = Directory.GetParent(Path.GetDirectoryName(model)!)!.Parent!.Parent!.FullName;
        await ComponentLicenseGate.EnsureAsync(MusicModelVariants.Components(MusicStudioRuntime.Variation), token);
        if (profile.SeparateNormalization) await ComponentLicenseGate.EnsureAsync(MusicStudioRuntime.CurrentComponentId, token);
        await MusicStudioRuntime.VerifyAsync(profile, token); await MusicStudioRuntime.VerifyModelsAsync(modelsRoot, token);
        await Task.Run(() => request.CheckContext(new MusicTokenizer(MusicTokenizerMetadata.Read(model, token)), token), token);
        var choice = await MusicHardwareProbe.CheckAsync(MusicYueRuntime.DirectoryPath, model,
            MusicModelVariants.Artifact(modelsRoot, MusicStudioRuntime.Variation, MusicComponentCatalog.DecoderId), request, !plan, token, Emit);
        // The original policy is a preflight estimate. Studio independently validates its device and may fall back.
        HardwareChanged?.Invoke(choice); LastHardware = "Studio auto; preflight=" + choice.Device.Backend + ": " + choice.Device.Description;
        LastRuntimePack = profile.Pack;
        await using (var file = new FileStream(requestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
            await JsonSerializer.SerializeAsync(file, body, cancellationToken: token); file.Flush(true);
        }
        await using var lease = await MusicStudioLease.StartAsync(modelsRoot, requestPath + ".studio", choice.Demand.ContextTokens, Emit, token, profile);
        var route = plan ? "v1/scores" : "v1/music/jobs";
        var submitted = await lease.SendAsync(route, body, token);
        var id = submitted.GetProperty("id").GetString() ?? throw new InvalidDataException("Studio job ID missing.");
        if (id.Length > 128 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) throw new InvalidDataException("Invalid Studio job ID.");
        var last = "";
        try { while (true)
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
                    if (profile.SeparateNormalization && (effective.ValueKind != JsonValueKind.Object
                        || effective.GetProperty("output_format").GetString() != "wav32"
                        || effective.GetProperty("companion_scale").GetDouble() != 1
                        || effective.GetProperty("solver").GetString() != request.EffectiveExpert.StudioSolver))
                        throw new InvalidDataException("Studio effective decoder policy differs from the submitted request.");
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
                        if (profile.SeparateNormalization) {
                            var processing = await MusicStudioOutput.NormalizeAndDecodeAsync(lease, song,
                                request.EffectiveExpert.Integer("peak_clip"), temporary, Emit, token);
                            var receipt = JsonSerializer.Serialize(new { schema = 2, engine_set = profile.EngineSetId,
                                runtime_revision = profile.Revision, studio_source = profile.SourceRevision, engine_revision = profile.EngineRevision,
                                generation_settings = status.GetProperty("generation_settings"),
                                ignored_generation_fields = new[] { "peak_clip" },
                                submitted_policy = new { companion_scale = 1, lyric_timing = false, solver = request.EffectiveExpert.StudioSolver },
                                processing, conversion = "Studio normalized FLAC24 -> FFmpeg PCM16 stereo 48000 Hz" });
                            if (receipt.Length > 1_048_576) throw new InvalidDataException("Oversized Studio request receipt.");
                            await File.WriteAllTextAsync(requestPath + ".processing.json", processing.GetRawText(), token);
                            LastRequestReceipt = receipt;
                        } else await lease.CopyAudioAsync(song.GetProperty("audio_url").GetString()!, temporary, token);
                        _ = MusicWaveFile.ReadDuration(temporary);
                    }
                    File.Move(temporary, output, false);
                } finally { if (File.Exists(temporary)) File.Delete(temporary); }
                await lease.ReadLogsAsync(token); return;
            }
            await Task.Delay(500, token);
        } }
        catch (OperationCanceledException) when (token.IsCancellationRequested) {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await lease.SendAsync(route + "/" + id, new { }, stop.Token); }
            catch (Exception error) { Emit("[Studio] cancel acknowledgement: " + error.GetType().Name); }
            throw;
        }
        // Disposal then stops the owned process tree before the runner confirms pause.
    }
    private void Emit(string text)
    {
        if (text.StartsWith("[Load]", StringComparison.Ordinal) && text.Contains("backend:", StringComparison.Ordinal)
            && _devices.Add(text)) LastHardware += "; actual=" + text;
        try { Log?.Invoke(text[..Math.Min(text.Length, 4096)]); }
        catch (Exception error) { OwnedProcessRegistry.Log("music_log_callback_failed", "Music.Studio", detail: error.GetType().Name); }
    }
}
