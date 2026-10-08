using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIHub.Services;

/// <summary>CPU-only, replaceable shared FFmpeg bundle. No system codecs or network protocols.</summary>
public sealed class MusicAudioRuntime(string directory)
{
    public const string ComponentId = "music.audio";
    public const string Revision = "ffmpeg-8.1-opus-1.5.2-lame-3.100-lopata-audio-1";
    public static MusicAudioRuntime Default { get; } = new(Path.Combine(AppContext.BaseDirectory, "MusicAudioRuntime"));
    public async Task PrepareAsync(CancellationToken token)
    {
        await ComponentLicenseGate.EnsureAsync(ComponentId, token);
        using var input = File.OpenRead(Path.Combine(directory, "manifest.json"));
        using var manifest = await JsonDocument.ParseAsync(input, cancellationToken: token);
        if (manifest.RootElement.GetProperty("Revision").GetString() != Revision) throw new InvalidDataException("Unsupported audio runtime.");
        var required = new[] { "ffmpeg.exe", "ffprobe.exe", "avcodec-62.dll", "avformat-62.dll", "avutil-60.dll", "avfilter-11.dll", "swresample-6.dll" };
        var files = manifest.RootElement.GetProperty("Files").EnumerateArray().ToArray();
        if (files.Length != required.Length) throw new InvalidDataException("Incomplete audio runtime.");
        foreach (var name in required) {
            var entry = files.Single(f => f.GetProperty("Name").GetString() == name);
            using var file = File.OpenRead(Path.Combine(directory, name));
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, token));
            if (!hash.Equals(entry.GetProperty("Sha256").GetString(), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Audio runtime checksum mismatch: " + name);
        }
    }
    public async Task<string> RunAsync(bool probe, IEnumerable<string> arguments, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var start = new ProcessStartInfo(Path.Combine(directory, probe ? "ffprobe.exe" : "ffmpeg.exe")) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
            StandardErrorEncoding = Encoding.UTF8, StandardOutputEncoding = Encoding.UTF8, WorkingDirectory = directory };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        token.ThrowIfCancellationRequested();
        using var process = OwnedProcessRegistry.Shared.Start(start, "Music.Audio");
        var output = ReadAsync(process.StandardOutput, 4 * 1024 * 1024);
        var diagnostic = ReadAsync(process.StandardError, 65536);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None); await Task.WhenAll(output, diagnostic);
            token.ThrowIfCancellationRequested(); throw new TimeoutException("Audio codec operation timed out.");
        }
        var text = await output; var error = await diagnostic;
        if (process.ExitCode != 0) throw new InvalidDataException("Audio codec failed: " + error);
        return text;
    }
    private static async Task<string> ReadAsync(StreamReader reader, int limit)
    {
        var text = new StringBuilder(); var buffer = new char[4096]; int count;
        while ((count = await reader.ReadAsync(buffer)) != 0) {
            text.Append(buffer, 0, count); if (text.Length > limit) text.Remove(0, text.Length - limit);
        }
        return text.ToString();
    }
    public async Task DecodeAsync(string source, string destination, CancellationToken token)
    {
        await PrepareAsync(token);
        await RunAsync(false, ["-nostdin", "-v", "error", "-n", "-i", source, "-map", "0:a:0", "-map_metadata", "-1",
            "-c:a", "pcm_s16le", "-ar", "48000", "-ac", "2", "-f", "wav", destination], token);
    }
}
