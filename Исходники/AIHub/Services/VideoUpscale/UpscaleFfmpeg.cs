using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AIHub.Services.VideoUpscale;

/// <summary>Bundled LGPL build with the actual filters and lossless codecs required by this utility.</summary>
internal static class UpscaleFfmpeg
{
    internal const string Revision = "8.1.3-video-upscale-2";
    internal static string DirectoryPath => Path.Combine(AppContext.BaseDirectory, "VideoUpscaleRuntime");
    internal static async Task PrepareAsync(CancellationToken token)
    {
        await ComponentLicenseGate.EnsureAsync("video.ffmpeg", token);
        using var manifest = typeof(UpscaleFfmpeg).Assembly.GetManifestResourceStream("AIHub.VideoUpscaleFfmpeg")!;
        var files = JsonSerializer.Deserialize<PinnedTreeFile[]>(manifest) ?? throw new IOException("VideoEditor.RuntimeMissing");
        await RuntimeFileAudit.VerifyAsync("runtime.video-upscale.ffmpeg", DirectoryPath, Revision, files, token);
    }
    internal static ProcessStartInfo Info(bool probe, IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(Path.Combine(DirectoryPath, probe ? "ffprobe.exe" : "ffmpeg.exe")) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true, StandardErrorEncoding = Encoding.UTF8, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var arg in args) info.ArgumentList.Add(arg); return info;
    }
    internal static async Task<string> RunAsync(bool probe, IEnumerable<string> args, CancellationToken token)
    {
        using var process = OwnedProcessRegistry.Shared.Start(Info(probe, args), "VideoUpscale.FFmpeg");
        var stderr = Read(process.StandardError); var stdout = Read(process.StandardOutput);
        using var stop = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        await process.WaitForExitAsync(CancellationToken.None); var error = await stderr; var output = await stdout;
        token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw new IOException("VideoUpscale.NativeFailed: " + error);
        return output;
        static async Task<string> Read(StreamReader reader) {
            StringBuilder tail = new();
            while (await reader.ReadLineAsync() is {} line) { tail.AppendLine(line); if (tail.Length > 1_000_000) tail.Remove(0, 500_000); }
            return tail.ToString();
        }
    }
    internal static async Task<JsonDocument> ProbeAsync(string path, CancellationToken token) => JsonDocument.Parse(await RunAsync(true, ["-v", "error", "-show_streams", "-show_format", "-of", "json", path], token));
}
