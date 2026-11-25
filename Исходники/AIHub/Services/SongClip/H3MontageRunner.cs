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
        var s = attempt.Settings;
        var deno = SongClipModes.IsDeno(attempt);
        var m = deno ? SongClipDeno.Generation(s) : s.Montage ?? new();
        var validSize = deno ? SongClipDeno.Sizes.Contains(SongClipDeno.Output(s) with { Accelerate = false })
            : s.Width is >= 256 and <= 3840 && s.Height is >= 256 and <= 2160 && s.Width % 2 == 0 && s.Height % 2 == 0;
        if (!SongClipModes.IsMontage(attempt) || attempt.Model != H3MusicRunner.ModelId || attempt.PlanningVersion != 1
            || s.Fps != 24 || !validSize
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
        var cards = SongClipVariantCatalog.Cards(SongClipModes.IsDeno(attempt) ? SongClipDeno.Mode : SongClipModes.Montage,
            attempt.ModelsRoot, SongClipDeno.Accelerated(attempt));
        await H3MontageBinding.EnsureAsync(attempt, store, job, cards, token);
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
        void Log(string message) => progress(0, message);
        var accelerated = SongClipDeno.Accelerated(attempt);
        var deno = SongClipModes.IsDeno(attempt)
            ? await SongClipDenoComponents.PrepareAsync(attempt.ModelsRoot, attempt.Connections, token, Log, accelerated) : null;
        var environment = deno?.Montage
            ?? await H3MontageComponents.PrepareAsync(attempt.ModelsRoot, attempt.Connections, token, Log);
        await RuntimeFileAudit.VerifyAsync(deno is null ? "SongClip.H3Montage.Adapter" : "SongClip.Deno.Adapter", Path.Combine(AppContext.BaseDirectory, "Tools"),
            ApplicationBuildVersion.Value, H3MontageBinding.AdapterFiles(deno is not null, accelerated), token);
        var payload = Request(attempt, store, job, environment, deno);
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
        var worker = accelerated ? SongClipDeno.AcceleratedWorker : deno is not null ? SongClipDeno.Worker : null;
        await H3MusicRunner.RunWorkerAsync(environment.Motion.H3.Python.Python, request, job,
            (value, message) => progress(.08 + (deno is null ? .90 : .82) * value, message), token,
            montage: true, verify: File.Exists(receiptPath), worker: worker);
        using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(receiptPath, token));
        await H3MontageReceipt.ValidateAsync(receipt.RootElement, request, attempt, store, plan, token);
        var models = H3MusicRunner.Facts(environment.Cards).Concat(plan.Assistants)
            .Append(new(H3MontageComponents.SourceId, "H3 Any Song montage", H3MontageComponents.SourceRevision, "beat analysis and montage routing"));
        if (deno is not null)
            models = models.Concat(environment.Cards.Where(card => card.ModelArtifactId is
                "video-h3-upscale-model" or "video-h3-source-upscaler" or SongClipDenoComponents.VfxId)
                .Select(card => new OutputModelUse(card.ModelArtifactId, card.DisplayName, card.Revision,
                    card.ModelArtifactId == SongClipDenoComponents.VfxId ? "RTX denoising and output finishing" : "progressive H3 latent upscaling")));
        if (accelerated)
        {
            var torch = environment.Cards.Single(card => card.ModelArtifactId == SongClipDenoComponents.SolTorchId);
            models = models.Append(new(torch.ModelArtifactId, torch.DisplayName, torch.Revision, "accelerated H3 runtime"));
        }
        var expectedModels = models.ToArray();
        if (File.Exists(final)) return await H3MusicRunner.CheckResult(final, attempt, store, expectedModels, token);
        var raw = receipt.RootElement.GetProperty("output").GetString() ?? throw new InvalidDataException("Missing montage output.");
        raw = RuntimeFileAudit.Contained(job, Path.GetRelativePath(job, Path.GetFullPath(raw)));
        if (deno is not null)
        {
            progress(.90, "SongClip.DenoFinishing");
            await H3MusicRunner.RunWorkerAsync(environment.Motion.H3.Python.Python, request, job,
                (value, message) => progress(.90 + .08 * value, message), token,
                montage: true, worker: SongClipDeno.FinishWorker, action: "--finish");
            raw = RuntimeFileAudit.Contained(job, "deno-finished.mp4");
        }
        var facts = new OutputProvenance {
            Scenario = "song-to-clip/h3_music_context", CreatedUtc = DateTimeOffset.UtcNow, GenerationSeconds = elapsed(),
            ModelHistory = accelerated ? "recorded-inference; deno; acceleration enabled"
                : deno is null ? "recorded-inference; montage" : "recorded-inference; deno", Models = expectedModels
        };
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

    private static JsonObject Request(SongClipAttempt attempt, SongClipStore store, string job, H3MontageEnvironment environment,
        SongClipDenoEnvironment? deno)
    {
        var h3 = environment.Motion.H3; var settings = attempt.Settings;
        var montage = deno is null ? settings.Montage ?? new() : SongClipDeno.Generation(settings);
        var intermediate = deno is null ? new(settings.Width, settings.Height) : SongClipDeno.Intermediate(settings);
        var accelerated = SongClipDeno.Accelerated(attempt);
        var names = VideoH3Profile.Models(false);
        var pins = environment.Cards.ToDictionary(card => card.ModelArtifactId, card => card.Revision);
        if (deno is not null)
        {
            var adapters = H3MontageBinding.AdapterFiles(true, accelerated);
            pins.Add("lopata-deno-adapter", adapters.Single(file => file.Path == SongClipDeno.Worker).Sha256);
            if (accelerated)
                pins.Add("lopata-deno-sol-adapter", adapters.Single(file => file.Path == SongClipDeno.AcceleratedWorker).Sha256);
        }
        var request = JsonSerializer.SerializeToNode(new {
            generationMode = "montage", libraries = h3.Libraries, comfy = h3.Comfy, motionSource = environment.Motion.MotionSource,
            analysisLibraries = environment.AnalysisLibraries,
            anySongSource = environment.AnySongSource, anySongSourceRevision = H3MontageComponents.SourceRevision, job,
            song = store.Resolve(attempt.ProjectId, attempt.Song),
            reference = SongClipReferences.For(attempt).FirstOrDefault() is { } first ? store.Resolve(attempt.ProjectId, first.File) : "",
            references = H3MusicRunner.ReferencePayload(attempt, store) ?? [],
            clipSeconds = montage.ClipSeconds, width = montage.Width, height = montage.Height, steps = montage.Steps, seed = settings.Seed,
            outputWidth = intermediate.Width, outputHeight = intermediate.Height, quality = settings.Quality,
            ffmpeg = Path.Combine(VideoRuntime.DirectoryPath, "ffmpeg.exe"), ffprobe = Path.Combine(VideoRuntime.DirectoryPath, "ffprobe.exe"),
            device = h3.Python.Device, model = names["Diffusion"], encoder = names["Encoder"], videoVae = names["VideoVae"], audioVae = names["AudioVae"],
            modelFolders = VideoH3Profile.Folders(h3, false), modelId = H3MusicRunner.ModelId, sourceRevision = H3MusicComponents.SourceRevision,
            pins, memoryMode = "auto", vramReserveGb = accelerated ? 4 : 2
        })!.AsObject();
        if (H3MusicRunner.ReferenceStorage(attempt, store, job) is { } referenceStorage)
            request["referenceStorage"] = JsonSerializer.SerializeToNode(referenceStorage);
        if (deno is not null)
        {
            var output = SongClipDeno.Output(settings);
            request["deno"] = JsonSerializer.SerializeToNode(new {
                width = output.Width, height = output.Height, upscaler = h3.Upscaler,
                kjNodes = h3.KjNodes, libraries = deno.Attention.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries),
                vfx = deno.Vfx
            });
            if (accelerated)
                request["deno"]!["torch"] = deno.AcceleratedTorch
                    ?? throw new InvalidDataException("Missing accelerated Deno runtime.");
        }
        return request;
    }
}
