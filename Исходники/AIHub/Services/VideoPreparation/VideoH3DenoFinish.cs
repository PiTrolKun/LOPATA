using System.Globalization;
using System.IO;
using System.Text.Json;
using AIHub.Services.SongClip;
using AIHub.Services.VideoEditor;

namespace AIHub.Services.VideoPreparation;

internal static class VideoH3DenoFinish
{
    internal static (int Width, int Height) Intermediate(string aspect)
    {
        var a = int.Parse(aspect.AsSpan(0, aspect.IndexOf(':')), CultureInfo.InvariantCulture);
        var b = int.Parse(aspect.AsSpan(aspect.IndexOf(':') + 1), CultureInfo.InvariantCulture);
        var height = a < b ? 1080 * b / (double)a : 1080;
        var width = height * a / b;
        var fit = Math.Min(1d, 1920 / Math.Max(width, height));
        return (2 * (int)Math.Round(width * fit / 2), 2 * (int)Math.Round(height * fit / 2));
    }

    internal static async Task<string> RunAsync(ManagedPythonSelection python, SongClipDenoRuntime runtime,
        string work, string source, string filter, int width, int height, int factor, int frames, int quality,
        bool audio, Action<string> report, CancellationToken token)
    {
        var adapter = H3MontageBinding.AdapterFiles(deno: true);
        await RuntimeFileAudit.VerifyAsync("SongClip.Deno.Adapter", Path.Combine(AppContext.BaseDirectory, "Tools"),
            ApplicationBuildVersion.Value, adapter, token, report);
        Directory.CreateDirectory(work);
        var input = RuntimeFileAudit.Contained(work, "source-1080.mkv");
        var result = RuntimeFileAudit.Contained(work, "deno-finished.mp4");
        try
        {
            List<string> args = ["-nostdin", "-v", "error", "-y", "-i", source, "-map", "0:v:0"];
            args.AddRange(audio ? ["-map", "0:a:0", "-c:a", "copy"] : ["-an"]);
            args.AddRange(["-map_metadata", "-1", "-vf", filter, "-frames:v", frames.ToString(CultureInfo.InvariantCulture),
                "-c:v", "h264_nvenc", "-gpu", python.Device.Split(':')[1], "-preset", "p4", "-tune", "lossless", input]);
            await VideoRuntime.RunAsync(false, args, token);
            SongClipStore.Atomic(RuntimeFileAudit.Contained(work, "worker-result.json"), new
            {
                output = input, outputSha256 = await H3MontageBinding.HashAsync(input, token),
                frames, duration = Math.BitDecrement(frames / 24d)
            });
            var request = RuntimeFileAudit.Contained(work, "finish-request.json");
            SongClipStore.Atomic(request, new
            {
                job = work, outputWidth = width, outputHeight = height, quality, audio, preparedSource = true,
                deno = new { width = width * factor, height = height * factor, vfx = runtime.Vfx },
                ffmpeg = Path.Combine(VideoRuntime.DirectoryPath, "ffmpeg.exe"),
                ffprobe = Path.Combine(VideoRuntime.DirectoryPath, "ffprobe.exe"),
                device = python.Device
            });
            await H3MusicRunner.RunWorkerAsync(python.Python, request, work, (_, message) => report("[H3 event] " + message), token,
                montage: true, worker: SongClipDeno.FinishWorker, action: "--finish");
            return result;
        }
        finally { File.Delete(input); }
    }

    private static OutputModelUse Model(SongClipDenoRuntime runtime)
    {
        var card = runtime.Cards.Single(card => card.ModelArtifactId == SongClipDenoComponents.VfxId);
        return new(card.ModelArtifactId, card.DisplayName, card.Revision, "RTX denoising and output finishing");
    }

    internal static async Task<(string File, OutputProvenance Facts)> WorkshopAsync(VideoH3Job job,
        ManagedPythonSelection python, SongClipDenoRuntime runtime, string directory, string source,
        Func<double> elapsed, Action<string> log, CancellationToken token)
    {
        var (width, height) = Intermediate(job.Settings.Aspect);
        var factor = job.Deno!.Output == 2160 ? 2 : 1;
        var audio = job.Settings.AudioMode == "native";
        using var probe = await VideoRuntime.ProbeAsync(source, token);
        var stream = probe.RootElement.GetProperty("streams").EnumerateArray().Single(s => s.GetProperty("codec_type").GetString() == "video");
        var original = JsonSerializer.Deserialize<OutputProvenance>(probe.RootElement.GetProperty("format")
            .GetProperty("tags").GetProperty("LOPATA_VIDEO").GetString()!) ?? throw new InvalidDataException("VideoPrep.H3InvalidResult");
        if (!int.TryParse(stream.GetProperty("nb_frames").GetString(), CultureInfo.InvariantCulture, out var frames) || frames <= 0)
            throw new InvalidDataException("VideoPrep.H3InvalidResult");
        log("[H3] deno-finishing");
        var cleaned = await RunAsync(python, runtime, RuntimeFileAudit.Contained(directory, "rtx"), source,
            $"scale={width}:{height}:flags=lanczos:force_original_aspect_ratio=increase,crop={width}:{height},setsar=1,format=yuv420p",
            width, height, factor, frames, VideoH3Quality.ToPercent(job.Settings.Crf), audio, log, token);
        var path = RuntimeFileAudit.Contained(directory, $"output/deno_{Guid.NewGuid():N}.mp4");
        var facts = original with
        {
            Models = [.. original.Models, Model(runtime)],
            GenerationSeconds = original.GenerationSeconds is null ? null : elapsed()
        };
        try
        {
            List<string> args = ["-nostdin", "-v", "error", "-n", "-i", cleaned, "-map", "0", "-c", "copy",
                "-metadata", "encoded_by=LOPATA " + facts.ProgramVersion, "-metadata", "LOPATA_VIDEO=" + facts.Json(),
                "-metadata", "LOPATA_OPERATION=" + job.Run.Id];
            if (facts.GenerationSeconds is { } seconds)
                args.AddRange(["-metadata", "LOPATA_GENERATION_SECONDS=" + seconds.ToString("0.000", CultureInfo.InvariantCulture)]);
            args.AddRange(["-movflags", "+faststart+use_metadata_tags", path]);
            await VideoRuntime.RunAsync(false, args, token);
            using var verification = await VideoRuntime.ProbeAsync(path, token);
            var streams = verification.RootElement.GetProperty("streams").EnumerateArray().ToArray();
            var video = streams.Single(s => s.GetProperty("codec_type").GetString() == "video");
            if (video.GetProperty("width").GetInt32() != width * factor || video.GetProperty("height").GetInt32() != height * factor
                || video.GetProperty("avg_frame_rate").GetString() != "24/1"
                || video.GetProperty("nb_frames").GetString() != frames.ToString(CultureInfo.InvariantCulture)
                || streams.Any(s => s.GetProperty("codec_type").GetString() == "audio") != audio
                || verification.RootElement.GetProperty("format").GetProperty("tags").GetProperty("LOPATA_VIDEO").GetString() != facts.Json())
                throw new InvalidDataException("VideoPrep.H3InvalidResult");
            return (path, facts);
        }
        catch { File.Delete(path); throw; }
    }
}
