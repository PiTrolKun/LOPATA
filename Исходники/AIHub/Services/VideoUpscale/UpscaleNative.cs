using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services.VideoEditor;

namespace AIHub.Services.VideoUpscale;

internal static class UpscaleNative
{
    internal static async Task Run(UpscaleStep step, string input, string output, IReadOnlyList<ManagedModelArtifactCard> cards, CancellationToken token, Action<string> log)
    {
        if (step.Engine == UpscaleEngine.Scale) {
            var filter = UpscaleMediaTools.ScaleFilter(step.Width, step.Height, step.Algorithm);
            if (UpscaleCatalog.Number(step, "denoise", "0") is > 0 and var noise) filter = $"hqdn3d={UpscaleMediaTools.N(noise)}," + filter;
            if (UpscaleCatalog.Number(step, "sharpen", "0") is > 0 and var sharp) filter += $",unsharp=5:5:{UpscaleMediaTools.N(sharp)}:5:5:0";
            await UpscaleMediaTools.Filter(input, output, filter, token); return;
        }
        var raw = output + ".native.mkv";
        if (step.Engine == UpscaleEngine.Flash) {
            var runtime = UpscaleRuntime.Expanded(cards.Single(c => c.ModelArtifactId == "video-upscale-flash-runtime"));
            var source = UpscaleRuntime.Expanded(cards.Single(c => c.ModelArtifactId == "video-upscale-flash-source"));
            var weights = cards.Single(c => c.ModelArtifactId == UpscaleComponents.WeightId(step));
            var media = await UpscaleMediaTools.Describe(input, token);
            var size = UpscaleCatalog.Fit(media.Width, media.Height, step.Width, step.Height);
            Dictionary<string, object> options = [];
            foreach (var f in UpscaleCatalog.FlashParameters.Where(f => f.Key is not ("mode" or "precision" or "device" or "seed"))) {
                var value = step.Get(f.Key, f.Default);
                options[f.Key] = bool.TryParse(value, out var b) ? b : f.Min is not null || f.Key == "temporal_compression" ?
                    (value.Contains('.') ? (object)double.Parse(value, System.Globalization.CultureInfo.InvariantCulture) : int.Parse(value, System.Globalization.CultureInfo.InvariantCulture)) : value;
            }
            var request = output + ".request.json";
            UpscaleStore.Save(request, new { source, weights = weights.InstallDirectory, input, output = raw,
                ffmpeg = Path.Combine(UpscaleFfmpeg.DirectoryPath, "ffmpeg.exe"), mode = step.Get("mode", "turbo"), precision = step.Get("precision", "int8_convrot"),
                device = step.Get("device", "auto"), seed = (int)UpscaleCatalog.Number(step, "seed", "1"), options,
                content = new[] { size.Width, size.Height }, canvas = new[] { (size.Width + 127) / 128 * 128, (size.Height + 127) / 128 * 128 } });
            var info = Info(Path.Combine(runtime, "python.exe"), ["-B", Path.Combine(AppContext.BaseDirectory, "Tools", "VideoUpscale", "worker.py"), request]);
            info.Environment["PYTHONNOUSERSITE"] = "1"; info.Environment.Remove("PYTHONPATH"); info.Environment.Remove("PYTHONHOME");
            info.Environment["PYTHONUTF8"] = "1"; info.Environment["PYTHONIOENCODING"] = "utf-8";
            var cache = Path.Combine(UpscaleStore.Root, "RuntimeCache"); Directory.CreateDirectory(cache);
            info.Environment["TRITON_CACHE_DIR"] = Path.Combine(cache, "triton"); info.Environment["TORCHINDUCTOR_CACHE_DIR"] = Path.Combine(cache, "torch");
            await Execute(info, token, log, output + ".log");
        } else {
            var root = UpscaleRuntime.Expanded(cards.Single(c => c.ModelArtifactId == "video-upscale-visual-enhancer"));
            var executable = Directory.EnumerateFiles(root, "VE_CLI.exe", SearchOption.AllDirectories).Single();
            var media = await UpscaleMediaTools.Describe(input, token);
            Dictionary<string, object> settings = new() {
                ["video_enabled_stages"] = new[] { step.Engine switch { UpscaleEngine.NeuralRendering => "neural_model", UpscaleEngine.RtxVideo => "super_resolution", UpscaleEngine.DlssSr => "dlss_super_resolution", UpscaleEngine.VfxVsr => "vfx_vsr", _ => "vfx_upscale" } },
                ["upscale_engine"] = step.Engine == UpscaleEngine.DlssSr ? "DLSS" : "RTX Video Super Resolution",
                ["upscale_vsr_enabled"] = true, ["upscale_size_mode"] = "Scale factor",
                ["upscale_scale_factor"] = Math.Max(1, Math.Max(step.Width / (double)media.Width, step.Height / (double)media.Height)),
                ["upscaling_factor"] = Math.Max(step.Width / (double)media.Width, step.Height / (double)media.Height),
                ["vfx_frame_generation_enabled"] = false,
                ["vfx_vsr_enabled"] = step.Engine == UpscaleEngine.VfxVsr, ["vfx_upscale_enabled"] = step.Engine == UpscaleEngine.VfxUpscale,
                ["vfx_size_mode"] = "Scale factor", ["vfx_scale_factor"] = Math.Max(step.Width / (double)media.Width, step.Height / (double)media.Height),
                ["vfx_upscale_scale"] = Math.Max(step.Width / (double)media.Width, step.Height / (double)media.Height)
            };
            foreach (var p in UpscaleCatalog.Parameters(step.Engine).Where(p => p.Key != "gpu")) {
                var value = step.Get(p.Key, p.Default);
                settings[p.Key] = bool.TryParse(value, out var b) ? b : p.Min is not null ? (object)double.Parse(value, System.Globalization.CultureInfo.InvariantCulture) : value;
            }
            var preset = output + ".preset.json";
            UpscaleStore.Save(preset, new { format = "dlss5-visual-enhancer-settings-preset", schema_version = 41, name = "LOPATA", settings });
            await Execute(Info(executable, ["render", input, "-o", raw, "--preset", preset, "--gpu", step.Get("gpu", "auto"), "--codec", "ffv1", "--container", "mkv", "--quality", "max", "--json", "--quiet"]), token, log, output + ".log");
        }
        await UpscaleMediaTools.Filter(raw, output, UpscaleMediaTools.ScaleFilter(step.Width, step.Height), token);
        File.Delete(raw);
    }
    private static ProcessStartInfo Info(string executable, IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = Path.GetDirectoryName(executable)! };
        foreach (var arg in args) info.ArgumentList.Add(arg); return info;
    }
    internal static async Task Execute(ProcessStartInfo info, CancellationToken token, Action<string> log, string? logFile = null)
    {
        using var journal = logFile is null ? null : new StreamWriter(logFile, append: true, Encoding.UTF8) { AutoFlush = true };
        var gate = new object();
        journal?.WriteLine(DateTimeOffset.UtcNow + " " + info.FileName + " " + JsonSerializer.Serialize(info.ArgumentList));
        using var process = OwnedProcessRegistry.Shared.Start(info, "VideoUpscale.Native");
        using var stop = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var stdout = Read(process.StandardOutput); var stderr = Read(process.StandardError);
        await process.WaitForExitAsync(CancellationToken.None); await stdout; var error = await stderr;
        journal?.WriteLine($"{DateTimeOffset.UtcNow:o} PID={process.Id} exit={process.ExitCode}");
        token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw new IOException("VideoUpscale.NativeFailed (" + process.ExitCode + "): " + error);
        async Task<string> Read(StreamReader reader) {
            StringBuilder tail = new();
            while (await reader.ReadLineAsync() is {} line) { lock (gate) journal?.WriteLine(line); log(line); tail.AppendLine(line); if (tail.Length > 32768) tail.Remove(0, tail.Length - 16384); }
            return tail.ToString();
        }
    }
}
