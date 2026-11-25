using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AIHub.Services.VideoUpscale;

/// <summary>Bundled LGPL media tools shared by video upscale, Shorts and the video converter.</summary>
internal static class UpscaleFfmpeg
{
    internal const string Revision = "8.1.3-video-upscale-5";
    internal static string DirectoryPath => Path.Combine(AppContext.BaseDirectory, "VideoUpscaleRuntime");
    internal static async Task PrepareAsync(CancellationToken token)
    {
        await ComponentLicenseGate.EnsureAsync("video.converter.ffmpeg", token);
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
    internal static async Task<string> RunAsync(bool probe, IEnumerable<string> args, CancellationToken token,
        Action<string>? outputLine = null, string operation = "VideoUpscale.FFmpeg")
    {
        token.ThrowIfCancellationRequested();
        using var process = OwnedProcessRegistry.Shared.Start(Info(probe, args), operation);
        var stderr = Read(process.StandardError); var stdout = Read(process.StandardOutput, outputLine);
        using var stop = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        await process.WaitForExitAsync(CancellationToken.None); var error = await stderr; var output = await stdout;
        token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw new IOException(operation + ".NativeFailed: " + error);
        return output;
        static async Task<string> Read(StreamReader reader, Action<string>? lineRead = null) {
            StringBuilder tail = new();
            while (await reader.ReadLineAsync() is {} line) { lineRead?.Invoke(line); tail.AppendLine(line); if (tail.Length > 1_000_000) tail.Remove(0, 500_000); }
            return tail.ToString();
        }
    }
    internal static async Task<JsonDocument> ProbeAsync(string path, CancellationToken token) => JsonDocument.Parse(await RunAsync(true, ["-v", "error", "-show_streams", "-show_format", "-of", "json", path], token));
}
