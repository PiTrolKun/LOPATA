using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHub.Services.VideoEditor;
using AIHub.Services.VideoPreparation;

namespace AIHub.Services.SongClip;

internal static class GeekatplayRunner
{
    internal const string ModelId = "geekatplay_music_video";
    private const string Scenario = "song-to-clip/geekatplay";
    internal static void ValidateSettings(SongClipAttempt attempt)
    {
        SongClipReferences.ValidateNewAttempt(attempt);
        var s = attempt.Settings; var g = s.Geek ?? new();
        if (attempt.Model != ModelId || attempt.PlanningVersion != 1 || s.Fps != 24 || s.Width is < 256 or > 3840
            || s.Height is < 256 or > 2160 || s.Width % 2 != 0 || s.Height % 2 != 0 || s.Quality is < 1 or > 100
            || s.Seed is < 0 or > 9_007_199_254_740_991 || g.Width is < 256 or > 1536 || g.Height is < 256 or > 1536
            || g.Width % 32 != 0 || g.Height % 32 != 0 || g.Steps is < 8 or > 50 || g.CardSteps is < 8 or > 40
            || g.ClipSeconds is < 5 or > 10 || !double.IsFinite(g.ReferenceStrength) || g.ReferenceStrength is < 0 or > 1)
            throw new InvalidDataException("SongClip.InvalidSettings");
    }
    internal static async Task<SongClipResult> RunAsync(SongClipAttempt attempt, SongClipStore store,
        Action<double, string> progress, CancellationToken token)
    {
        ValidateSettings(attempt);
        using var audit = ComponentAudit.CaptureOperation();
        using var auditLog = ComponentAudit.CaptureLog(message => progress(0, message));
        var job = store.Resolve(attempt.ProjectId, "attempts/" + Guid.ParseExact(attempt.Id, "N").ToString("N"));
        Directory.CreateDirectory(job);
        await GeekatplayBinding.EnsureAsync(attempt, store, job, GeekatplayComponents.Cards(attempt.ModelsRoot), token);
        var clock = Path.Combine(job, "elapsed.json");
        var previous = File.Exists(clock) ? JsonSerializer.Deserialize<double>(await File.ReadAllTextAsync(clock, token)) : 0;
        if (!double.IsFinite(previous) || previous < 0) throw new InvalidDataException("Invalid generation clock.");
        var watch = Stopwatch.StartNew(); double saved = 0;
        void Report(double value, string message) {
            if (watch.Elapsed.TotalSeconds - saved >= 2) { SongClipStore.Atomic(clock, previous + watch.Elapsed.TotalSeconds); saved = watch.Elapsed.TotalSeconds; }
            progress(value, message);
        }
        try { return await Render(attempt, store, job, Report, token, () => previous + watch.Elapsed.TotalSeconds); }
        finally { SongClipStore.Atomic(clock, previous + watch.Elapsed.TotalSeconds); }
    }
    private static async Task<SongClipResult> Render(SongClipAttempt attempt, SongClipStore store, string job,
        Action<double, string> progress, CancellationToken token, Func<double> elapsed)
    {
        progress(0, "SongClip.Preparing");
        await ApplicationBackgroundOperations.RetireModelsAsync();
        await VideoRuntime.PrepareAsync(token);
        var environment = await GeekatplayComponents.PrepareAsync(attempt.ModelsRoot, attempt.Connections, token, message => progress(0, message));
        await RuntimeFileAudit.VerifyAsync("SongClip.Geek.Adapter", Path.Combine(AppContext.BaseDirectory, "Tools"),
            ApplicationBuildVersion.Value, GeekatplayBinding.AdapterFiles(), token);
        var request = Request(attempt, store, job, environment);
        var analysisRequest = Path.Combine(job, "geek-analysis-request.json");
        SongClipStore.Atomic(analysisRequest, request);
        await Worker("--analyze", analysisRequest);
        using var analysis = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(job, "geek-analysis.json"), token));
        var slots = Slots(attempt, analysis.RootElement);
        var plan = await SongClipPlanning.EnsureAsync(attempt, store, job, progress, token, slots)
            ?? throw new InvalidDataException(SongClipVisualProtocol.InvalidReply);
        request["visualPlan"] = JsonSerializer.SerializeToNode(plan);
        var input = Path.Combine(job, "request.json");
        if (File.Exists(input)) {
            if (!JsonNode.DeepEquals(JsonNode.Parse(await File.ReadAllTextAsync(input, token)), request))
                throw new InvalidDataException("SongClip.VisualPlanMismatch");
        }
        else { token.ThrowIfCancellationRequested(); SongClipStore.Atomic(input, request); }
        await ApplicationBackgroundOperations.RetireModelsAsync();
        await Worker(File.Exists(Path.Combine(job, "worker-result.json")) ? "--verify" : "--run", input);
        using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(job, "worker-result.json"), token));
        var value = receipt.RootElement;
        if (value.GetProperty("schema").GetInt32() != 1 || value.GetProperty("modelId").GetString() != ModelId
            || value.GetProperty("sourceRevision").GetString() != GeekatplayComponents.Revision
            || value.GetProperty("requestSha256").GetString() != await H3MontageBinding.HashAsync(input, token)
            || value.GetProperty("cards").GetInt32() != slots.Length + 1 || value.GetProperty("clips").GetInt32() != slots.Length
            || value.GetProperty("frames").GetInt32() != analysis.RootElement.GetProperty("slots").EnumerateArray().Sum(s => s.GetProperty("frames").GetInt32()))
            throw new InvalidDataException("Invalid Geekatplay receipt.");
        var raw = RuntimeFileAudit.Contained(job, Path.GetRelativePath(job, value.GetProperty("output").GetString()!));
        if (value.GetProperty("sha256").GetString() != await H3MontageBinding.HashAsync(raw, token))
            throw new InvalidDataException("Geekatplay result checksum mismatch.");
        var facts = new OutputProvenance { Scenario = Scenario, CreatedUtc = DateTimeOffset.UtcNow, GenerationSeconds = elapsed(),
            ModelHistory = "recorded-inference; storyboard cards and FL2VA", Models = [..Facts(environment), ..plan.Assistants] };
        var final = Path.Combine(job, "clip.mp4");
        if (!File.Exists(final)) {
            var staging = Path.Combine(job, "clip.installing.mp4"); progress(.99, "SongClip.Finalizing");
            await VideoRuntime.RunAsync(false, ["-nostdin", "-v", "error", "-y", "-i", raw, "-map", "0:v:0", "-map", "0:a:0",
                "-c", "copy", "-metadata", "encoded_by=LOPATA " + facts.ProgramVersion, "-metadata", "LOPATA_VIDEO=" + facts.Json(),
                "-metadata", "LOPATA_OPERATION=" + attempt.Id, "-metadata", "creation_time=" + facts.ExportedUtc.ToString("O"),
                "-movflags", "+faststart+use_metadata_tags", staging], token);
            await Check(staging); token.ThrowIfCancellationRequested(); File.Move(staging, final, true);
        }
        return await Check(final);

        Task Worker(string action, string path) => H3MusicRunner.RunWorkerAsync(environment.H3.Python.Python, path, job,
            progress, token, worker: "song_clip_geek.py", action: action);
        async Task<SongClipResult> Check(string path) {
            using var output = await VideoRuntime.ProbeAsync(path, token);
            using var source = await VideoRuntime.ProbeAsync(store.Resolve(attempt.ProjectId, attempt.Song), token);
            var seconds = H3MusicResultValidation.Validate(output.RootElement, source.RootElement, attempt.Settings, attempt.Id, facts.Models, Scenario);
            return new(attempt.Id, "attempts/" + attempt.Id + "/clip.mp4", ModelId, seconds, DateTimeOffset.UtcNow);
        }
    }
    internal static SongClipVisualScene[] Slots(SongClipAttempt attempt, JsonElement analysis)
    {
        if (analysis.GetProperty("schema").GetInt32() != 1) throw new InvalidDataException("Invalid Geekatplay analysis.");
        var duration = analysis.GetProperty("durationSeconds").GetDouble();
        if (!double.IsFinite(duration) || duration is <= 0 or > 7200) throw new InvalidDataException("Invalid song duration.");
        var rows = analysis.GetProperty("slots").EnumerateArray().ToArray();
        if (rows.Length is < 1 or > 256) throw new InvalidDataException("Invalid Geekatplay segment count.");
        var source = SongClipVisualProtocol.Source(attempt); var result = new List<SongClipVisualScene>(); double end = 0;
        foreach (var row in rows) {
            var index = row.GetProperty("index").GetInt32(); var start = row.GetProperty("startSeconds").GetDouble();
            var stop = row.GetProperty("endSeconds").GetDouble(); var frames = row.GetProperty("frames").GetInt32();
            if (index != result.Count + 1 || !double.IsFinite(start) || !double.IsFinite(stop) || Math.Abs(start - end) > .0001
                || stop <= start || stop - start > 15 || Math.Abs((stop - start) * 24 - frames) > .0001)
                throw new InvalidDataException("Invalid Geekatplay beat boundaries.");
            end = stop; stop = Math.Min(stop, duration);
            result.Add(new(index, start, stop, "", source.Timings.Where(c => c.StartSeconds >= start && c.StartSeconds < stop).Select(c => c.Text).Distinct().ToArray()));
        }
        if (end < duration || end - duration > 1d / 24 + .0001) throw new InvalidDataException("Incomplete Geekatplay song coverage.");
        return result.ToArray();
    }
    private static OutputModelUse[] Facts(GeekatplayEnvironment environment) => environment.Cards
        .Where(c => c.ModelArtifactId is GeekatplayComponents.ModelId or "song-clip-geek-shared-models" or "song-clip-glitch-sdxl" or GeekatplayComponents.SourceId)
        .Select(c => new OutputModelUse(c.ModelArtifactId, c.DisplayName, c.Revision, c.ModelArtifactId switch {
            GeekatplayComponents.ModelId => "FL2VA diffusion", "song-clip-glitch-sdxl" => "storyboard image generation",
            GeekatplayComponents.SourceId => "song analysis and card/FL2VA graphs", _ => "text/image encoder and video VAE" })).ToArray();
    internal static JsonObject Request(SongClipAttempt attempt, SongClipStore store, string job, GeekatplayEnvironment environment)
    {
        var h3 = environment.H3; var g = attempt.Settings.Geek ?? new();
        var shared = environment.Cards.Single(c => c.ModelArtifactId == "song-clip-geek-shared-models");
        var diffusion = environment.Cards.Single(c => c.ModelArtifactId == GeekatplayComponents.ModelId);
        return JsonSerializer.SerializeToNode(new { job, programVersion = ApplicationBuildVersion.Value, libraries = h3.Libraries, comfy = h3.Comfy,
            analysisLibraries = environment.AnalysisLibraries, geekSource = environment.Source, geekRevision = GeekatplayComponents.Revision,
            sdxl = environment.Sdxl, song = store.Resolve(attempt.ProjectId, attempt.Song), references = H3MusicRunner.ReferencePayload(attempt, store) ?? [],
            width = g.Width, height = g.Height, steps = g.Steps, cardSteps = g.CardSteps, clipSeconds = g.ClipSeconds,
            referenceStrength = g.ReferenceStrength, seed = attempt.Settings.Seed, outputWidth = attempt.Settings.Width,
            outputHeight = attempt.Settings.Height, quality = attempt.Settings.Quality, device = h3.Python.Device,
            ffmpeg = Path.Combine(VideoRuntime.DirectoryPath, "ffmpeg.exe"), ffprobe = Path.Combine(VideoRuntime.DirectoryPath, "ffprobe.exe"),
            model = "minimax_h3_fl2va_pruned_int8_convrot.safetensors", encoder = "qwen3vl_32b_minimax_h3_int8_convrot.safetensors",
            videoVae = "minimax_h3_video_vae_int8_convrot.safetensors", modelId = ModelId,
            modelFolders = new Dictionary<string, string> { ["diffusion_models"] = Path.Combine(diffusion.InstallDirectory, "diffusion_models"),
                ["text_encoders"] = Path.Combine(shared.InstallDirectory, "text_encoders"), ["vae"] = Path.Combine(shared.InstallDirectory, "vae") },
            pins = environment.Cards.ToDictionary(c => c.ModelArtifactId, c => c.Revision)
        })!.AsObject();
    }
}
