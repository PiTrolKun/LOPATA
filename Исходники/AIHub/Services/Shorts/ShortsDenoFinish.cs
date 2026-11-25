using System.IO;
using System.Globalization;
using System.Text.Json.Nodes;
using AIHub.Services.SongClip;
using AIHub.Services.VideoEditor;

namespace AIHub.Services.Shorts;

internal static class ShortsDenoFinish
{
    internal static async Task<(string File, OutputModelUse Model)> RunAsync(ShortsJob job, string source, string crop,
        Action<string> report, CancellationToken token)
    {
        var profile = job.Description?.Deno ?? throw new InvalidDataException("Shorts.InvalidJob");
        var python = await ManagedPythonRuntime.ResolveAsync("cuda", token, log: report);
        var runtime = await SongClipDenoComponents.PrepareRuntimeAsync(job.ModelsRoot, job.Connections,
            python, profile.Accelerate, report, token);
        var adapter = H3MontageBinding.AdapterFiles(deno: true);
        await RuntimeFileAudit.VerifyAsync("SongClip.Deno.Adapter", Path.Combine(AppContext.BaseDirectory, "Tools"),
            ApplicationBuildVersion.Value, adapter, token, report);

        var work = ShortsJob.Assets.Resolve(job.Id, "rtx");
        Directory.CreateDirectory(work);
        var input = RuntimeFileAudit.Contained(work, "vertical.mkv");
        var result = RuntimeFileAudit.Contained(work, "deno-finished.mp4");
        try
        {
            // The RTX finisher reads lossless 1080p input and keeps the generated soundtrack.
            await VideoRuntime.RunAsync(false, ["-nostdin", "-v", "error", "-y", "-i", source,
                "-map", "0:v:0", "-map", "0:a:0", "-map_metadata", "-1",
                "-vf", $"{crop},scale={ShortsJob.Width}:{ShortsJob.Height}:flags=lanczos,setsar=1,format=yuv420p",
                "-frames:v", job.OutputFrames.ToString(CultureInfo.InvariantCulture),
                "-t", job.OutputSeconds.ToString("0.#########", CultureInfo.InvariantCulture),
                "-c:v", "h264_nvenc", "-gpu", python.Device.Split(':')[1], "-preset", "p4", "-tune", "lossless",
                "-c:a", "copy", input], token);
            SongClipStore.Atomic(RuntimeFileAudit.Contained(work, "worker-result.json"), new
            {
                output = input, outputSha256 = await H3MontageBinding.HashAsync(input, token),
                frames = job.OutputFrames, duration = Math.BitDecrement(job.OutputSeconds)
            });
            var request = RuntimeFileAudit.Contained(work, "finish-request.json");
            SongClipStore.Atomic(request, new
            {
                job = work, outputWidth = ShortsJob.Width, outputHeight = ShortsJob.Height, quality = 88,
                deno = new { width = ShortsJob.Width, height = ShortsJob.Height, vfx = runtime.Vfx },
                ffmpeg = Path.Combine(VideoRuntime.DirectoryPath, "ffmpeg.exe"),
                ffprobe = Path.Combine(VideoRuntime.DirectoryPath, "ffprobe.exe"),
                device = python.Device
            });
            await H3MusicRunner.RunWorkerAsync(python.Python, request, work, (_, message) => report(message), token,
                montage: true, worker: SongClipDeno.FinishWorker, action: "--finish");
            var vfx = runtime.Cards.Single(card => card.ModelArtifactId == SongClipDenoComponents.VfxId);
            return (result, new(vfx.ModelArtifactId, vfx.DisplayName, vfx.Revision, "RTX denoising and output finishing"));
        }
        finally { File.Delete(input); }
    }
}
