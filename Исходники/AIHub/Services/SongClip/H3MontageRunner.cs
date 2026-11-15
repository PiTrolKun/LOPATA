using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHub.Services.VideoEditor;
using AIHub.Services.VideoPreparation;

namespace AIHub.Services.SongClip;

/// <summary>Same managed H3 lease and models, with upstream Any Song montage routing.</summary>
internal static class H3MontageRunner
{
    internal static void ValidateSettings(SongClipAttempt attempt)
    {
        SongClipReferences.ValidateNewAttempt(attempt);
        var s = attempt.Settings; var m = s.Montage ?? new SongClipMontageSettings();
        if (!SongClipModes.IsMontage(attempt) || attempt.Model != H3MusicRunner.ModelId || attempt.PlanningVersion != 1
            || s.Fps != 24 || s.Width is < 256 or > 3840 || s.Height is < 256 or > 2160 || s.Width % 2 != 0 || s.Height % 2 != 0
            || s.Quality is < 1 or > 100 || m.Width is < 256 or > 1536 || m.Height is < 256 or > 1536
            || m.Width % 32 != 0 || m.Height % 32 != 0 || m.Steps is < 8 or > 50 || m.ClipSeconds is < 2 or > 15
            || attempt.References is null && !string.IsNullOrWhiteSpace(attempt.Reference))
            throw new InvalidDataException("SongClip.InvalidSettings");
    }
    internal static async Task<SongClipResult> RunAsync(SongClipAttempt attempt, SongClipStore store,
        Action<double, string> progress, CancellationToken token, bool measureWarmOverlap = false, string? plannerId = null)
    {
        ValidateSettings(attempt);
        using var audit = ComponentAudit.CaptureOperation();
        using var auditLog = ComponentAudit.CaptureLog(message => progress(0, message));
        await VideoRuntime.PrepareAsync(token);
        var job = store.Resolve(attempt.ProjectId, "attempts/" + Guid.ParseExact(attempt.Id, "N").ToString("N"));
        Directory.CreateDirectory(job);
        await H3MontageBinding.EnsureAsync(attempt, store, job, H3MontageComponents.Cards(attempt.ModelsRoot), token);
        var clock = Path.Combine(job, "elapsed.json");
        var prior = File.Exists(clock) ? JsonSerializer.Deserialize<double>(await File.ReadAllTextAsync(clock, token)) : 0;
        if (!double.IsFinite(prior) || prior < 0) throw new InvalidDataException("Invalid generation clock.");
        var watch = Stopwatch.StartNew(); double saved = 0;
        double Elapsed() => prior + watch.Elapsed.TotalSeconds;
        void Report(double value, string message) {
            if (watch.Elapsed.TotalSeconds - saved >= 2) { SongClipResources.SaveElapsed(clock, Elapsed()); saved = watch.Elapsed.TotalSeconds; }
            progress(value, message);
        }
        try { return await RenderAsync(attempt, store, job, Report, token, Elapsed, measureWarmOverlap, plannerId); }
        finally { SongClipResources.SaveElapsed(clock, Elapsed()); }
    }

    private static async Task<SongClipResult> RenderAsync(SongClipAttempt attempt, SongClipStore store, string job,
        Action<double, string> progress, CancellationToken token, Func<double> elapsed, bool measureWarmOverlap, string? plannerId)
    {
        progress(0, "SongClip.Preparing");
        await ApplicationBackgroundOperations.RetireModelsAsync();
        if (!File.Exists(Path.Combine(job, "worker-result.json")))
            await SongClipResources.CheckH3AudioAsync(store.Resolve(attempt.ProjectId, attempt.Song), token);
        var environment = await H3MontageComponents.PrepareAsync(attempt.ModelsRoot, attempt.Connections, token, message => progress(0, message));
        await RuntimeFileAudit.VerifyAsync("SongClip.H3Montage.Adapter", Path.Combine(AppContext.BaseDirectory, "Tools"),
            ApplicationBuildVersion.Value, H3MontageBinding.AdapterFiles(), token);
        var payload = Request(attempt, store, job, environment);
        var final = Path.Combine(job, "clip.mp4");
        var plan = await PreparePlanAsync(attempt, store, job, payload, environment.Motion.H3.Python.Python,
            progress, token, measureWarmOverlap, plannerId);
        if (plan is null || plan.Assistants is not { Length: > 0 }) throw new InvalidDataException(SongClipMontageProtocol.InvalidReply);
        payload["montagePlan"] = JsonSerializer.SerializeToNode(plan);
        var request = Path.Combine(job, "request.json");
        if (File.Exists(request)) {
            var previous = JsonNode.Parse(await File.ReadAllTextAsync(request, token));
            if (!JsonNode.DeepEquals(previous, payload)) throw new InvalidDataException(SongClipMontageProtocol.Mismatch);
        }
        else { token.ThrowIfCancellationRequested(); SongClipStore.Atomic(request, payload); }
        var receiptPath = Path.Combine(job, "worker-result.json");
        if (!File.Exists(receiptPath))
            await H3MusicRunner.RunWorkerAsync(environment.Motion.H3.Python.Python, request, job,
                (value, message) => progress(.08 + .90 * value, message), token, montage: true);
        else await H3MusicRunner.RunWorkerAsync(environment.Motion.H3.Python.Python, request, job,
            (value, message) => progress(.08 + .90 * value, message), token, montage: true, verify: true);
        using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(receiptPath, token));
        await H3MontageReceipt.ValidateAsync(receipt.RootElement, request, attempt, store, plan, token);
        var facts = new OutputProvenance {
            Scenario = "song-to-clip/h3_music_context", CreatedUtc = DateTimeOffset.UtcNow, GenerationSeconds = elapsed(),
            ModelHistory = "recorded-inference; montage", Models = [..H3MusicRunner.Facts(environment.Cards), ..plan.Assistants,
                new(H3MontageComponents.SourceId, "H3 Any Song montage", H3MontageComponents.SourceRevision, "beat analysis and montage routing")]
        };
        if (File.Exists(final)) return await H3MusicRunner.CheckResult(final, attempt, store, facts.Models, token);
        var raw = receipt.RootElement.GetProperty("output").GetString() ?? throw new InvalidDataException("Missing montage output.");
        raw = RuntimeFileAudit.Contained(job, Path.GetRelativePath(job, Path.GetFullPath(raw)));
        var staging = Path.Combine(job, "clip.installing.mp4"); progress(.99, "SongClip.Finalizing");
        SongClipResources.CheckMuxSpace(raw, staging);
        await VideoRuntime.RunAsync(false, ["-nostdin", "-v", "error", "-y", "-i", raw,
            "-map", "0:v:0", "-map", "0:a:0", "-c", "copy", "-metadata", "encoded_by=LOPATA " + facts.ProgramVersion,
            "-metadata", "LOPATA_VIDEO=" + facts.Json(), "-metadata", "LOPATA_OPERATION=" + attempt.Id,
            "-metadata", "creation_time=" + facts.ExportedUtc.ToString("O"), "-movflags", "+faststart+use_metadata_tags", staging], token);
        _ = await H3MusicRunner.CheckResult(staging, attempt, store, facts.Models, token);
        token.ThrowIfCancellationRequested(); File.Move(staging, final, true);
        return await H3MusicRunner.CheckResult(final, attempt, store, facts.Models, token);
    }

    internal static async Task<SongClipMontagePlan> PreparePlanAsync(SongClipAttempt attempt, SongClipStore store,
        string job, JsonObject request, string python, Action<double, string> progress, CancellationToken token,
        bool warmDuringAnalysis, string? plannerId = null)
    {
        var analysisFile = Path.Combine(job, "montage-analysis.json");
        var analyze = !File.Exists(analysisFile);
        using var early = CancellationTokenSource.CreateLinkedTokenSource(token);
        var warming = warmDuringAnalysis && analyze && !File.Exists(Path.Combine(job, "montage-plan-binding.json"))
            ? WarmPlannerAsync(attempt, early.Token, plannerId) : null;
        try
        {
            if (analyze)
            {
                var analysisRequest = Path.Combine(job, "montage-analysis-request.json");
                SongClipStore.Atomic(analysisRequest, request);
                await H3MusicRunner.RunWorkerAsync(python, analysisRequest, job,
                    (value, message) => progress(.04 * value, message), token, montage: true, analyze: true);
            }
            var analysis = JsonSerializer.Deserialize<SongClipMontageAnalysis>(await File.ReadAllTextAsync(analysisFile, token))
                ?? throw new InvalidDataException(SongClipMontageProtocol.Mismatch);
            analysis.Validate();
            if (File.Exists(Path.Combine(job, "clip.mp4")))
                return await SongClipMontagePlanning.ReadCompletedAsync(attempt, store, job, analysis, token, plannerId);

            SongClipPlannerSession? warmed = null;
            if (warming is not null)
            {
                try { warmed = await warming; }
                catch (IOException failure) when (NativeHardwareFailure.IsRecoverable(failure.Message))
                { OwnedProcessRegistry.Log("montage_warm_overlap_fallback", "SongClip", detail: failure.Message); }
            }
            return await SongClipMontagePlanning.EnsureAsync(attempt, store, job, analysis,
                (value, message) => progress(.04 + .04 * value, message), token, warmed, plannerId);
        }
        finally
        {
            if (warming is not null)
            {
                await early.CancelAsync();
                try { await (await warming).DisposeAsync(); }
                catch (Exception) when (warming.IsFaulted || warming.IsCanceled) { }
            }
        }
    }

    private static async Task<SongClipPlannerSession> WarmPlannerAsync(SongClipAttempt attempt, CancellationToken token, string? plannerId)
    {
        var session = await SongClipPlannerModel.OpenAsync(attempt, token, plannerId);
        try { await session.WarmAsync(token); return session; }
        catch { await session.DisposeAsync(); throw; }
    }

    private static JsonObject Request(SongClipAttempt attempt, SongClipStore store, string job, H3MontageEnvironment environment)
    {
        var h3 = environment.Motion.H3; var settings = attempt.Settings; var montage = settings.Montage ?? new SongClipMontageSettings();
        var names = VideoH3Profile.Models(false);
        return JsonSerializer.SerializeToNode(new {
            generationMode = "montage", libraries = h3.Libraries, comfy = h3.Comfy, motionSource = environment.Motion.MotionSource,
            analysisLibraries = environment.AnalysisLibraries,
            anySongSource = environment.AnySongSource, anySongSourceRevision = H3MontageComponents.SourceRevision, job,
            song = store.Resolve(attempt.ProjectId, attempt.Song),
            reference = SongClipReferences.For(attempt).FirstOrDefault() is { } first ? store.Resolve(attempt.ProjectId, first.File) : "",
            references = H3MusicRunner.ReferencePayload(attempt, store) ?? [],
            clipSeconds = montage.ClipSeconds, width = montage.Width, height = montage.Height, steps = montage.Steps, seed = settings.Seed,
            outputWidth = settings.Width, outputHeight = settings.Height, quality = settings.Quality,
            ffmpeg = Path.Combine(VideoRuntime.DirectoryPath, "ffmpeg.exe"), ffprobe = Path.Combine(VideoRuntime.DirectoryPath, "ffprobe.exe"),
            device = h3.Python.Device, model = names["Diffusion"], encoder = names["Encoder"], videoVae = names["VideoVae"], audioVae = names["AudioVae"],
            modelFolders = VideoH3Profile.Folders(h3, false), modelId = H3MusicRunner.ModelId, sourceRevision = H3MusicComponents.SourceRevision,
            pins = environment.Cards.ToDictionary(card => card.ModelArtifactId, card => card.Revision), memoryMode = "auto", vramReserveGb = 2
        })!.AsObject();
    }
}
