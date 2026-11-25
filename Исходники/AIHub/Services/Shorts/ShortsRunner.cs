using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services.SongClip;
using AIHub.Services.VideoEditor;
using AIHub.Services.VideoPreparation;
using AIHub.Services.VideoUpscale;

namespace AIHub.Services.Shorts;

internal static class ShortsRunner
{
    internal static Task<string> StartAsync(ShortsJob job, string title, Action<string> report, CancellationToken token,
        BackgroundOperationState? restored = null) =>
        ApplicationBackgroundOperations.RunAsync(ShortsJob.Kind, title, job.Id, job.Id,
            ct => RunAsync(ShortsJob.Load(job.Id), report, ct), token, restored);

    internal static async Task<string> RunAsync(ShortsJob job, Action<string> report, CancellationToken token)
    {
        using var audit = ComponentAudit.CaptureOperation();
        using var auditLog = ComponentAudit.CaptureLog(report);
        var watch = Stopwatch.StartNew();
        await VideoRuntime.PrepareAsync(token);
        if (File.Exists(job.Output))
        {
            await VerifyAsync(job.Output, job, token);
            return job.Output;
        }

        report("Shorts.Preparing");
        if (job.SourceSha256 is null)
        {
            await ApplicationBackgroundOperations.RetireModelsAsync();
            await GenerateAsync(job, report, token);
            job = job with { SourceSha256 = await UpscaleMediaTools.Hash(job.SourcePath(), token) };
            job.Save();
            OwnedProcessRegistry.Log("shorts_generation_ready", "Shorts", detail: $"{job.Id} {watch.Elapsed.TotalSeconds:F1}s");
        }
        else if (await UpscaleMediaTools.Hash(job.SourcePath(), token) != job.SourceSha256)
            throw new InvalidDataException("Shorts.InvalidResult");

        await PublishAsync(job, job.SourcePath(), report, token);
        OwnedProcessRegistry.Log("shorts_result_ready", "Shorts", detail: $"{job.Id} {watch.Elapsed.TotalSeconds:F1}s");
        return job.Output;
    }

    private static async Task GenerateAsync(ShortsJob job, Action<string> report, CancellationToken token)
    {
        if (job.Song is { } song)
        {
            await H3MontageRunner.RunAsync(song, ShortsJob.Assets, (_, detail) => report(detail), token);
            return;
        }
        var request = job.Description ?? throw new InvalidDataException("Shorts.InvalidJob");
        if (request.Run.Complete) return;
        var output = Path.Combine(VideoH3Runner.DirectoryFor(request.Run.Id), "output");
        var project = new VideoPreparationProject
        {
            Id = request.ProjectId,
            Attachments = request.Run.Files.Select(file =>
                new VideoPreparationAttachment(RuntimeFileAudit.Contained(output, file.RelativePath), file.Role)).ToList()
        };
        VideoH3WorkspaceState.Ensure(project).Runs.Add(request.Run);
        await VideoH3Runner.RunAsync(request, project, () => { job.Save(); return Task.CompletedTask; }, report, token);
    }

    internal static async Task PublishAsync(ShortsJob job, string source, Action<string> report, CancellationToken token)
    {
        var stage = Stopwatch.StartNew();
        OutputProvenance original;
        string crop;
        using (var probe = await VideoRuntime.ProbeAsync(source, token))
        {
            var video = Stream(probe.RootElement, "video");
            if (Stream(probe.RootElement, "audio").ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Shorts.InvalidResult");
            crop = Crop(video.GetProperty("width").GetInt32(), video.GetProperty("height").GetInt32());
            original = Provenance(probe.RootElement);
        }
        if (ShortsGenerationProfile.FromJob(job).Deno)
        {
            if (job.Description?.Deno is not null)
            {
                report("SongClip.DenoFinishing");
                var finished = await ShortsDenoFinish.RunAsync(job, source, crop, report, token);
                source = finished.File;
                original = original with { Models = [.. original.Models, finished.Model] };
            }
            using var probe = await VideoRuntime.ProbeAsync(source, token);
            var video = Stream(probe.RootElement, "video");
            if (video.GetProperty("width").GetInt32() != ShortsJob.Width ||
                video.GetProperty("height").GetInt32() != ShortsJob.Height)
                throw new InvalidDataException("Shorts.InvalidResult");
            var canCopy = !job.FadeEnding && video.GetProperty("nb_frames").GetString() == job.OutputFrames.ToString(CultureInfo.InvariantCulture)
                && double.TryParse(probe.RootElement.GetProperty("format").GetProperty("duration").GetString(),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out var duration) && Math.Abs(duration - job.OutputSeconds) <= .001;
            report("Shorts.Exporting");
            await ExportAsync(job, source, "", original, null, token, direct: canCopy);
            return;
        }
        report("Shorts.Upscaling");
        var dlss = await TryDlssAsync(job, source, crop, report, token);
        OwnedProcessRegistry.Log("shorts_upscale_ready", "Shorts", detail: $"{job.Id} {stage.Elapsed.TotalSeconds:F1}s");
        stage.Restart();
        report("Shorts.Exporting");
        await ExportAsync(job, source, crop, original, dlss, token);
        if (dlss is { } completed)
        {
            try { File.Delete(completed.File); }
            catch (IOException error) { OwnedProcessRegistry.Log("shorts_cleanup_failed", "Shorts", detail: error.Message); }
        }
        OwnedProcessRegistry.Log("shorts_export_ready", "Shorts", detail: $"{job.Id} {stage.Elapsed.TotalSeconds:F1}s");
    }

    internal static async Task ExportAsync(ShortsJob job, string source, string crop, OutputProvenance original,
        (string File, OutputModelUse Model)? dlss, CancellationToken token, bool direct = false)
    {
        var file = job.Output;
        Lopata.Updates.SafeUpdatePath.RejectLinks(file);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var staging = Path.Combine(Path.GetDirectoryName(file)!, $".lopata-shorts-{job.Id}-{Guid.NewGuid():N}.partial.mp4");
        var facts = original with
        {
            Scenario = job.FromSong ? "shorts/from-song" : "shorts/from-description",
            ExportedUtc = DateTimeOffset.UtcNow, CreatedUtc = job.CreatedUtc,
            Models = dlss is { } enhanced ? [.. original.Models, enhanced.Model] : original.Models
        };
        var pipe = job.FadeEnding && dlss is not null;
        List<string> args = ["-nostdin", "-v", "error", "-y"];
        args.AddRange(pipe ? ShortsFadePipe.InputArguments : ["-i", dlss?.File ?? source]);
        if (dlss is not null) args.AddRange(["-i", source]);
        var normalize = (dlss is null && crop.Length > 0 ? crop + "," : "") +
            $"scale={ShortsJob.Width}:{ShortsJob.Height}:flags=lanczos,setsar=1,fps={ShortsJob.Fps},format=yuv420p";
        var videoFilter = pipe ? "setsar=1" : normalize;
        if (job.FadeEnding)
        {
            var fadeFrames = ShortsJob.Fps * ShortsJob.FadeSeconds;
            // The first frame of the last second starts the fade; its last displayed frame is black.
            videoFilter += FormattableString.Invariant($",fade=t=out:s={job.OutputFrames - fadeFrames}:n={fadeFrames - 1}");
            args.AddRange(["-af", ShortsJob.AudioFade(job.OutputSeconds)]);
        }
        args.AddRange(["-map", "0:v:0", "-map", dlss is null ? "0:a:0?" : "1:a:0?",
            "-map_metadata", "-1", "-t", job.OutputSeconds.ToString("0.#########", CultureInfo.InvariantCulture)]);
        args.AddRange(direct ? ["-c", "copy"] : ["-vf", videoFilter, "-c:v", "libopenh264",
            "-b:v", "12M", "-c:a", "aac", "-b:a", "192k"]);
        args.AddRange([
            "-metadata", "encoded_by=LOPATA " + facts.ProgramVersion, "-metadata", "LOPATA_VIDEO=" + facts.Json(),
            "-metadata", "LOPATA_OPERATION=" + job.Id, "-movflags", "+faststart+use_metadata_tags", staging]);
        try
        {
            if (pipe)
            {
                await ShortsFadePipe.RenderAsync(dlss!.Value.File, normalize, job.OutputFrames, args, token);
            }
            else if (job.FadeEnding || crop.Length == 0)
            {
                await VideoRuntime.PrepareAsync(token);
                await VideoRuntime.RunAsync(false, args, token);
            }
            else
            {
                await UpscaleFfmpeg.PrepareAsync(token);
                await UpscaleFfmpeg.RunAsync(false, args, token);
            }
            await VerifyAsync(staging, job, token);
            token.ThrowIfCancellationRequested();
            File.Move(staging, file, false);
            CollectionExportJournal.Record(file, facts.Scenario);
        }
        finally { File.Delete(staging); }
    }

    internal static string Crop(int width, int height)
    {
        // A multiple of two keeps 9:16 exact with even dimensions for YUV 4:2:0.
        var unit = Math.Min(width / 18, height / 32) * 2;
        if (unit <= 0) throw new InvalidDataException("Shorts.InvalidResult");
        return FormattableString.Invariant($"crop={9 * unit}:{16 * unit}:(iw-ow)/2:(ih-oh)/2");
    }

    private static async Task<(string File, OutputModelUse Model)?> TryDlssAsync(ShortsJob job, string source, string crop,
        Action<string> report, CancellationToken token)
    {
        var output = ShortsJob.Assets.Resolve(job.Id, "dlss.mkv");
        if (job.DlssCheckpoint is { } checkpoint && job.SourceSha256 is { } sourceSha &&
            sourceSha == checkpoint.SourceSha256 && File.Exists(output) &&
            await UpscaleMediaTools.Hash(output, token) == checkpoint.MediaSha256)
        {
            OwnedProcessRegistry.Log("shorts_dlss_reused", "Shorts", detail: job.Id);
            return (output, checkpoint.Model);
        }

        var step = ShortsComponents.DlssStep();
        var project = new UpscaleProject { Source = source, Steps = [step] };
        var cards = UpscaleComponents.Cards(job.ModelsRoot, project);
        if (SongClipAutomaticPreparation.NeedsDownload(cards))
        {
            report("Shorts.SoftwareScale");
            return null;
        }
        var input = ShortsJob.Assets.Resolve(job.Id, "portrait.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(input)!);
        await UpscaleFfmpeg.PrepareAsync(token);
        using var preparation = new UpscaleComponents(job.Connections);
        if (!await preparation.PrepareAsync(job.ModelsRoot, project, false, null, token))
        {
            report("Shorts.SoftwareScale");
            return null;
        }
        // Only the native call falls back. Input, audit and export errors retain their own diagnostics.
        await UpscaleMediaTools.Filter(source, input, crop + ",setsar=1", token);
        report("Shorts.Dlss");
        try
        {
            await UpscaleNative.Run(step, input, output, cards, token, report);

        }
        catch (IOException failure)
        {
            OwnedProcessRegistry.Log("shorts_dlss_fallback", "Shorts", detail: failure.Message);
            File.Delete(output); File.Delete(output + ".native.mkv");
            report("Shorts.SoftwareScale");
            return null;
        }
        finally { File.Delete(input); }

        var card = cards.Single();
        var model = new OutputModelUse(card.ModelArtifactId, card.DisplayName, card.Revision, "DLSS Super Resolution");
        if (job.SourceSha256 is { } sourceHash)
        {
            var mediaHash = await UpscaleMediaTools.Hash(output, token);
            (job with { DlssCheckpoint = new(sourceHash, mediaHash, model) }).Save();
        }
        return (output, model);
    }

    private static JsonElement Stream(JsonElement probe, string kind) => probe.GetProperty("streams").EnumerateArray()
        .FirstOrDefault(stream => stream.GetProperty("codec_type").GetString() == kind);

    private static OutputProvenance Provenance(JsonElement probe)
    {
        var tags = probe.GetProperty("format").GetProperty("tags");
        var tag = tags.EnumerateObject().FirstOrDefault(property => property.Name.Equals("LOPATA_VIDEO", StringComparison.OrdinalIgnoreCase));
        var facts = tag.Value.ValueKind == JsonValueKind.String ? JsonSerializer.Deserialize<OutputProvenance>(tag.Value.GetString()!) : null;
        return facts is { Program: "LOPATA", Models.Length: > 0 } ? facts : throw new InvalidDataException("Shorts.InvalidResult");
    }

    private static async Task VerifyAsync(string file, ShortsJob job, CancellationToken token)
    {
        using var check = await VideoRuntime.ProbeAsync(file, token);
        var probe = check.RootElement;
        var video = Stream(probe, "video");
        var audio = Stream(probe, "audio");
        var format = probe.GetProperty("format");
        var tags = format.GetProperty("tags");
        // MP4's container timebase can round fractional-frame excerpt endings to milliseconds.
        if (!format.TryGetProperty("duration", out var duration) ||
            !double.TryParse(duration.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
            !double.IsFinite(seconds) || seconds > ShortsJob.MaximumSeconds || Math.Abs(seconds - job.OutputSeconds) > .001
            || video.ValueKind != JsonValueKind.Object || video.GetProperty("width").GetInt32() != ShortsJob.Width
            || video.GetProperty("height").GetInt32() != ShortsJob.Height || video.GetProperty("codec_name").GetString() != "h264"
            || video.GetProperty("avg_frame_rate").GetString() != "24/1"
            || !video.TryGetProperty("nb_frames", out var frames) || frames.GetString() != job.OutputFrames.ToString(CultureInfo.InvariantCulture)
            || audio.ValueKind != JsonValueKind.Object || audio.GetProperty("codec_name").GetString() != "aac"
            || Math.Abs(ShortsJob.SongDuration(probe) - job.OutputSeconds) > 1d / ShortsJob.Fps
            || tags.GetProperty("LOPATA_OPERATION").GetString() != job.Id
            || Provenance(probe).Scenario != (job.FromSong ? "shorts/from-song" : "shorts/from-description"))
            throw new InvalidDataException("Shorts.InvalidResult");
    }
}
