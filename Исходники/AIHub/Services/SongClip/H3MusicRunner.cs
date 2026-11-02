using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIHub.Services.VideoEditor;
using AIHub.Services.VideoPreparation;

namespace AIHub.Services.SongClip;

/// <summary>Durable song attempt around the pinned upstream music-context sampler.</summary>
internal static class H3MusicRunner
{
    internal const string Kind = "song.clip.h3_music_context";
    internal const string ModelId = "h3_music_context";

    internal static async Task<SongClipResult> RunAsync(SongClipAttempt attempt, SongClipStore store,
        Action<double, string> progress, CancellationToken token)
    {
        ValidateSettings(attempt);
        if (SongClipModes.IsMontage(attempt)) return await H3MontageRunner.RunAsync(attempt, store, progress, token);
        using var audit = ComponentAudit.CaptureOperation();
        await VideoRuntime.PrepareAsync(token);
        var job = store.Resolve(attempt.ProjectId, "attempts/" + Guid.ParseExact(attempt.Id, "N").ToString("N"));
        Directory.CreateDirectory(job);
        var final = Path.Combine(job, "clip.mp4");
        var cards = H3MusicComponents.Cards(attempt.ModelsRoot);
        await H3MusicAttemptBinding.EnsureAsync(attempt, store, job, cards, token);
        var expected = Facts(cards);
        if (File.Exists(final)) {
            var plan = await SongClipPlanning.ReadCompletedAsync(attempt, store, job, token);
            if (attempt.PlanningVersion == 1 && plan?.Assistants is not { Length: > 0 }) throw new InvalidDataException(SongClipVisualProtocol.InvalidReply);
            if (attempt.References is not null) {
                using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(job, "worker-result.json"), token));
                await ValidateReferenceReceiptAsync(receipt.RootElement, attempt, store, plan, token);
            }
            return await CheckResult(final, attempt, store, [..expected, ..(plan?.Assistants ?? [])], token);
        }
        var clockFile = Path.Combine(job, "elapsed.json");
        var prior = File.Exists(clockFile) ? JsonSerializer.Deserialize<double>(await File.ReadAllTextAsync(clockFile, token)) : 0;
        if (!double.IsFinite(prior) || prior < 0) throw new InvalidDataException("Invalid generation clock.");
        var watch = Stopwatch.StartNew(); double saved = 0;
        double Elapsed() => prior + watch.Elapsed.TotalSeconds;
        void Report(double fraction, string message)
        {
            if (watch.Elapsed.TotalSeconds - saved >= 2)
            { SongClipStore.Atomic(clockFile, Elapsed()); saved = watch.Elapsed.TotalSeconds; }
            progress(fraction, message);
        }
        try { return await RunAttemptAsync(attempt, store, job, Report, token, Elapsed); }
        finally { SongClipStore.Atomic(clockFile, Elapsed()); }
    }

    internal static void ValidateSettings(SongClipAttempt attempt)
    {
        if (SongClipModes.IsMontage(attempt)) { H3MontageRunner.ValidateSettings(attempt); return; }
        SongClipReferences.ValidateNewAttempt(attempt);
        var settings = attempt.Settings; var motion = settings.Motion;
        if (attempt.Model != ModelId || settings.Width is < 256 or > 3840 || settings.Height is < 256 or > 2160
            || settings.Width % 2 != 0 || settings.Height % 2 != 0 || settings.Fps != 24
            || settings.Quality is < 1 or > 100 || attempt.PlanningVersion is < 0 or > 1
            || attempt.PlanningVersion == 0 && string.IsNullOrWhiteSpace(settings.Prompt)
            || motion.Width is < 256 or > 1536 || motion.Height is < 256 or > 1536
            || motion.Width % 32 != 0 || motion.Height % 32 != 0 || motion.Steps is < 8 or > 50
            || motion.RawFrames is < 124 or > 362 || (motion.RawFrames - 5) % 17 != 0
            || motion.ContextFrames < 39 || (motion.ContextFrames - 39) % 51 != 0
            || motion.ContextFrames >= motion.RawFrames)
            throw new InvalidDataException("SongClip.InvalidSettings");
    }

    private static async Task<SongClipResult> RunAttemptAsync(SongClipAttempt attempt, SongClipStore store, string job,
        Action<double, string> progress, CancellationToken token, Func<double> elapsed)
    {
        using var audit = ComponentAudit.CaptureOperation();
        using var auditLog = ComponentAudit.CaptureLog(message => progress(0, message));
        progress(0, "SongClip.Preparing");
        await ApplicationBackgroundOperations.RetireModelsAsync();
        var plan = await SongClipPlanning.EnsureAsync(attempt, store, job, progress, token);
        if (attempt.PlanningVersion == 1 && plan?.Assistants is not { Length: > 0 }) throw new InvalidDataException(SongClipVisualProtocol.InvalidReply);
        var environment = await H3MusicComponents.PrepareAsync(attempt.ModelsRoot, attempt.Connections, token, message => progress(0, message));
        var scripts = new[] { ("song_clip_motion.py", "AIHub.SongClip.MotionWorker"),
            ("song_clip_motion_state.py", "AIHub.SongClip.MotionState"), ("h3_memory.py", "AIHub.H3Memory") };
        var files = new List<PinnedTreeFile>();
        foreach (var (file, resource) in scripts)
        {
            using var source = typeof(H3MusicRunner).Assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidDataException("Missing H3 music adapter.");
            files.Add(new(file, source.Length, Convert.ToHexString(SHA256.HashData(source))));
        }
        await RuntimeFileAudit.VerifyAsync("SongClip.H3Motion.Adapter", Path.Combine(AppContext.BaseDirectory, "Tools"),
            ApplicationBuildVersion.Value, files, token);
        var receipt = Path.Combine(job, "worker-result.json");
        if (!File.Exists(receipt))
        {
            var h3 = environment.H3; var settings = attempt.Settings; var motion = settings.Motion;
            var names = VideoH3Profile.Models(false);
            var payload = new
            {
                libraries = h3.Libraries, comfy = h3.Comfy, motionSource = environment.MotionSource, job,
                song = store.Resolve(attempt.ProjectId, attempt.Song),
                reference = attempt.Reference.Length == 0 ? "" : store.Resolve(attempt.ProjectId, attempt.Reference),
                references = ReferencePayload(attempt, store),
                referencePrompts = attempt.References is { Length: > 0 } && plan is not null
                    ? plan.Scenes.Select(scene => SongClipReferencePrompt.Compose(attempt, plan, scene.Index)).ToArray() : null,
                prompt = settings.Prompt, visualPlan = plan, lyrics = attempt.Lyrics, timings = attempt.Timings,
                width = motion.Width, height = motion.Height, rawFrames = motion.RawFrames,
                contextFrames = motion.ContextFrames, steps = motion.Steps, seed = settings.Seed,
                outputWidth = settings.Width, outputHeight = settings.Height, quality = settings.Quality,
                ffmpeg = Path.Combine(VideoRuntime.DirectoryPath, "ffmpeg.exe"),
                ffprobe = Path.Combine(VideoRuntime.DirectoryPath, "ffprobe.exe"), device = h3.Python.Device,
                model = names["Diffusion"], encoder = names["Encoder"], videoVae = names["VideoVae"], audioVae = names["AudioVae"],
                modelFolders = VideoH3Profile.Folders(h3, false), modelId = ModelId,
                sourceRevision = H3MusicComponents.SourceRevision,
                pins = environment.Cards.ToDictionary(c => c.ModelArtifactId, c => c.Revision),
                memoryMode = "auto", vramReserveGb = 2
            };
            var request = Path.Combine(job, "request.json"); SongClipWorkerRequest.Write(request, payload, plan);
            await RunWorkerAsync(h3.Python.Python, request, job, progress, token);
        }
        using var rendered = JsonDocument.Parse(await File.ReadAllTextAsync(receipt, token));
        await ValidateReferenceReceiptAsync(rendered.RootElement, attempt, store, plan, token);
        var raw = rendered.RootElement.GetProperty("output").GetString() ?? throw new InvalidDataException("Missing H3 music output.");
        var relative = Path.GetRelativePath(job, Path.GetFullPath(raw));
        raw = RuntimeFileAudit.Contained(job, relative);
        var facts = new OutputProvenance
        {
            Scenario = "song-to-clip/h3_music_context", CreatedUtc = DateTimeOffset.UtcNow,
            GenerationSeconds = elapsed(), ModelHistory = "recorded-inference", Models = [..Facts(environment.Cards), ..(plan?.Assistants ?? [])]
        };
        var staging = Path.Combine(job, "clip.installing.mp4"); progress(.99, "SongClip.Finalizing");
        await VideoRuntime.RunAsync(false, ["-nostdin", "-v", "error", "-y", "-i", raw,
            "-map", "0:v:0", "-map", "0:a:0", "-c", "copy",
            "-metadata", "encoded_by=LOPATA " + facts.ProgramVersion, "-metadata", "LOPATA_VIDEO=" + facts.Json(),
            "-metadata", "LOPATA_OPERATION=" + attempt.Id, "-metadata", "creation_time=" + facts.ExportedUtc.ToString("O"),
            "-movflags", "+faststart+use_metadata_tags", staging], token);
        _ = await CheckResult(staging, attempt, store, facts.Models, token);
        token.ThrowIfCancellationRequested(); var final = Path.Combine(job, "clip.mp4"); File.Move(staging, final, true);
        return await CheckResult(final, attempt, store, facts.Models, token);
    }

    internal static object[]? ReferencePayload(SongClipAttempt attempt, SongClipStore store) => attempt.References?
        .Select(reference => (object)new { file = store.Resolve(attempt.ProjectId, reference.File),
            kind = reference.Kind, description = reference.Description }).ToArray();

    internal static async Task ValidateReferenceReceiptAsync(JsonElement receipt, SongClipAttempt attempt,
        SongClipStore store, SongClipVisualPlan? plan, CancellationToken token)
    {
        if (attempt.References is null) return;
        var references = new List<object>();
        foreach (var (reference, index) in attempt.References.Select((value, index) => (value, index + 1))) {
            await using var image = File.OpenRead(store.Resolve(attempt.ProjectId, reference.File));
            references.Add(new { index, kind = reference.Kind, description = reference.Description,
                sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(image, token)) });
        }
        if (attempt.References.Length > 0 && plan is null) throw new InvalidDataException(SongClipVisualProtocol.InvalidReply);
        string[] prompts = attempt.References.Length == 0 ? [] : plan!.Scenes.Select(scene => Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(SongClipReferencePrompt.Compose(attempt, plan!, scene.Index))))).ToArray();
        if (!receipt.TryGetProperty("referenceInputs", out var inputs)
            || !JsonElement.DeepEquals(inputs, JsonSerializer.SerializeToElement(references))
            || !receipt.TryGetProperty("referencePromptSha256", out var renderedPrompts)
            || !JsonElement.DeepEquals(renderedPrompts, JsonSerializer.SerializeToElement(prompts)))
            throw new InvalidDataException("H3 music reference receipt does not match the attempt images, roles and scene prompts.");
    }

    internal static async Task RunWorkerAsync(string python, string request, string job,
        Action<double, string> progress, CancellationToken token, bool montage = false, bool analyze = false, bool verify = false, string? worker = null, string? action = null)
    {
        var info = new ProcessStartInfo(python)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        var script = Path.Combine(AppContext.BaseDirectory, "Tools", worker ?? (montage ? "song_clip_montage.py" : "song_clip_motion.py"));
        ManagedPythonLaunch.ScriptArguments(info, action is not null ? [script, action, request] : analyze ? [script, "--analyze", request] : verify ? [script, "--verify", request] : [script, request]);
        var label = worker == "song_clip_geek.py" ? "SongClip.Geekatplay" : montage ? "SongClip.H3Montage" : "SongClip.H3Motion";
        using var process = OwnedProcessRegistry.Shared.Start(info, label);
        using var cancel = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var diagnostics = new StringBuilder();
        var stderr = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                await File.AppendAllTextAsync(Path.Combine(job, "stderr.log"), line + Environment.NewLine, CancellationToken.None);
                diagnostics.AppendLine(line); if (diagnostics.Length > 20000) diagnostics.Remove(0, 12000);
            }
        });
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                await File.AppendAllTextAsync(Path.Combine(job, "progress.jsonl"), line + Environment.NewLine, CancellationToken.None);
                if (!line.StartsWith("@motion ", StringComparison.Ordinal)) { progress(0, line); continue; }
                using var message = JsonDocument.Parse(line[8..]); var value = message.RootElement;
                var fraction = value.TryGetProperty("fraction", out var f) ? f.GetDouble() : 0;
                if (!double.IsFinite(fraction)) throw new InvalidDataException("Invalid H3 music progress.");
                // Keep event structure for the presentation layer; checkpoint payloads
                // belong in the private event log, not directly in the status label.
                progress(Math.Clamp(fraction, 0, .98), value.GetRawText());
            }
            await process.WaitForExitAsync(CancellationToken.None); await stderr; token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new IOException((worker == "song_clip_geek.py" ? "Geekatplay: " : "H3 Motion Context Music: ") + diagnostics);
            if (worker is null && !File.Exists(Path.Combine(job, analyze ? "montage-analysis.json" : "worker-result.json"))) throw new InvalidDataException("H3 music worker did not commit its result.");
        }
        finally
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None); await stderr;
        }
    }

    internal static OutputModelUse[] Facts(IReadOnlyList<AIHub.Models.ManagedModelArtifactCard> cards)
    {
        var model = cards.Single(c => c.ModelArtifactId == "video-h3-models");
        var names = VideoH3Profile.Models(false);
        return new[] { new OutputModelUse("video-h3-models/" + names["Diffusion"], names["Diffusion"], model.Revision, "video and audio diffusion"),
            new("video-h3-models/" + names["Encoder"], names["Encoder"], model.Revision, "text and image encoder"),
            new("video-h3-models/" + names["VideoVae"], names["VideoVae"], model.Revision, "video decoder"),
            new("video-h3-models/" + names["AudioVae"], names["AudioVae"], model.Revision, "audio encoder and decoder") }
            .Concat(cards.Where(c => c.ModelArtifactId is "video-h3-source-comfy" or "video-h3-runtime-libraries" or H3MusicComponents.SourceId)
                .Select(c => new OutputModelUse(c.ModelArtifactId, c.DisplayName, c.Revision, c.ModelArtifactId == H3MusicComponents.SourceId ? "music-context continuation" : "runtime"))).ToArray();
    }

    internal static async Task<SongClipResult> CheckResult(string file, SongClipAttempt attempt, SongClipStore store,
        IReadOnlyList<OutputModelUse> expected, CancellationToken token)
    {
        using var output = await VideoRuntime.ProbeAsync(file, token);
        using var source = await VideoRuntime.ProbeAsync(store.Resolve(attempt.ProjectId, attempt.Song), token);
        var seconds = H3MusicResultValidation.Validate(output.RootElement, source.RootElement, attempt.Settings, attempt.Id, expected);
        return new(attempt.Id, "attempts/" + attempt.Id + "/clip.mp4", ModelId, seconds, DateTimeOffset.UtcNow) {
            Mode = SongClipModes.Normalize(attempt.Settings.Motion.Mode) };
    }
}
