using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AIHub.Services.VideoEditor;

internal static class VideoRuntime
{
    public static string DirectoryPath => Path.Combine(AppContext.BaseDirectory, "VideoEditorRuntime");
    public static async Task PrepareAsync(CancellationToken token)
    {
        await ComponentLicenseGate.EnsureAsync("video.ffmpeg", token);
        using var input = typeof(VideoRuntime).Assembly.GetManifestResourceStream("AIHub.VideoEditorRuntimeManifest") ?? throw new IOException("VideoEditor.RuntimeMissing");
        var files = JsonSerializer.Deserialize<PinnedTreeFile[]>(input) ?? throw new IOException("VideoEditor.RuntimeMissing");
        await RuntimeFileAudit.VerifyAsync("runtime.video-editor.ffmpeg", DirectoryPath, "8.1.3-video-editor-1", files, token);
    }
    public static ProcessStartInfo Info(bool probe, IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(Path.Combine(DirectoryPath, probe ? "ffprobe.exe" : "ffmpeg.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, StandardErrorEncoding = Encoding.UTF8, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var arg in args) info.ArgumentList.Add(arg); return info;
    }
    public static async Task<string> RunAsync(bool probe, IEnumerable<string> args, CancellationToken token, Action<string>? line = null)
    {
        using var process = OwnedProcessRegistry.Shared.Start(Info(probe, args), probe ? "VideoEditor.Probe" : "VideoEditor.Render");
        var errors = ReadTail(process.StandardError, line); var output = ReadTail(process.StandardOutput, line);
        using var cancel = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        await process.WaitForExitAsync(CancellationToken.None); var stdout = await output; var stderr = await errors;
        token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw new IOException("VideoEditor.RuntimeFailed: " + stderr);
        return stdout;
    }
    private static async Task<string> ReadTail(StreamReader reader, Action<string>? line)
    {
        var text = new StringBuilder();
        while (await reader.ReadLineAsync() is { } s) { line?.Invoke(s); text.AppendLine(s); if (text.Length > 4 * 1024 * 1024) text.Remove(0, text.Length / 2); }
        return text.ToString();
    }
    public static async Task<JsonDocument> ProbeAsync(string path, CancellationToken token) => JsonDocument.Parse(await RunAsync(true, ["-v", "error", "-show_streams", "-show_format", "-of", "json", path], token));
}
