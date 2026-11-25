using System.IO;
using AIHub.Models;
using AIHub.Services.SongClip;

namespace AIHub.Services.VideoUpscale;

internal sealed class UpscaleDeno(string vfx, IReadOnlyDictionary<string, ManagedPythonSelection> devices)
{
    private static readonly HashSet<string> CudaProfiles = [
        HardwareRuntimeCatalog.PythonCudaId, HardwareRuntimeCatalog.PythonCuda128Id
    ];

    internal static bool SupportsSize(int width, int height) =>
        Math.Max(width, height) <= 3840 && Math.Min(width, height) >= (Math.Max(width, height) > 1920 ? 576 : 288);

    internal static async Task<UpscaleDeno> PrepareAsync(UpscaleStep[] steps, IReadOnlyList<ManagedModelArtifactCard> cards,
        IProgress<UpscaleProgress> progress, CancellationToken token)
    {
        var adapter = H3MontageBinding.AdapterFiles(deno: true);
        await RuntimeFileAudit.VerifyAsync("SongClip.Deno.Adapter", Path.Combine(AppContext.BaseDirectory, "Tools"),
            ApplicationBuildVersion.Value, adapter, token);
        var vfx = cards.Single(card => card.ModelArtifactId == SongClipDenoComponents.VfxId);
        var devices = new Dictionary<string, ManagedPythonSelection>();
        foreach (var policy in steps.Where(s => s.Engine == UpscaleEngine.Deno).Select(s => s.Get("device", "auto")).Distinct())
        {
            try
            {
                devices[policy] = await ManagedPythonRuntime.ResolveAsync(policy == "auto" ? "cuda" : policy, token, CudaProfiles,
                    line => progress.Report(new(0, line)), requireReliableGpuProbe: true);
            }
            catch (PythonHardwareProbeException error)
            {
                throw new IOException("VideoUpscale.DenoGpuRequired", error);
            }
        }
        return new(SongClipDenoComponents.PackageDirectory(vfx), devices);
    }

    internal async Task RunAsync(UpscaleStep step, string input, string output, string rate, long frames,
        Action<string> log, CancellationToken token)
    {
        var factor = Math.Max(step.Width, step.Height) > 1920 ? 2 : 1;
        var width = step.Width / (2 * factor) * 2;
        var height = step.Height / (2 * factor) * 2;
        var work = output + ".deno";
        var normalized = RuntimeFileAudit.Contained(work, "source.mkv");
        var nativeOutput = RuntimeFileAudit.Contained(work, "deno-finished.mkv");
        var device = devices[step.Get("device", "auto")];
        Lopata.Updates.SafeUpdatePath.RejectLinks(work);
        Directory.CreateDirectory(work);
        try
        {
            await UpscaleMediaTools.Filter(input, normalized, UpscaleMediaTools.ScaleFilter(width, height, step.Algorithm), token);
            SongClipStore.Atomic(RuntimeFileAudit.Contained(work, "worker-result.json"), new
            {
                output = normalized, outputSha256 = await H3MontageBinding.HashAsync(normalized, token), frames
            });
            var request = RuntimeFileAudit.Contained(work, "finish-request.json");
            SongClipStore.Atomic(request, new
            {
                job = work, outputWidth = width, outputHeight = height, preparedSource = true, lossless = true, audio = false, rate,
                deno = new { width = width * factor, height = height * factor, vfx },
                ffmpeg = Path.Combine(UpscaleFfmpeg.DirectoryPath, "ffmpeg.exe"),
                ffprobe = Path.Combine(UpscaleFfmpeg.DirectoryPath, "ffprobe.exe"),
                device = device.Device
            });
            log("VideoUpscale.DenoProcessing");
            await H3MusicRunner.RunWorkerAsync(device.Python, request, work, (_, line) => log(line), token,
                montage: true, worker: SongClipDeno.FinishWorker, action: "--finish");
            token.ThrowIfCancellationRequested();
            if ((step.Width, step.Height) == (width * factor, height * factor))
                File.Move(nativeOutput, output, true);
            else
                await UpscaleMediaTools.Filter(nativeOutput, output, UpscaleMediaTools.ScaleFilter(step.Width, step.Height), token);
        }
        catch (IOException error) when (error.Message == "SongClip.DenoGpuRequired")
        {
            throw new IOException("VideoUpscale.DenoGpuRequired", error);
        }
        finally
        {
            File.Delete(normalized);
            File.Delete(nativeOutput);
        }
    }
}
