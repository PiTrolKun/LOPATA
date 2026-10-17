using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIHub.Services.LyricVideo;

internal static class ClipRuntime
{
    public static string VideoDirectory => Path.Combine(AppContext.BaseDirectory, "CaptureRuntime");
    public static ProcessStartInfo Info(string executable, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }
    public static async Task PrepareAsync(CancellationToken token)
    {
        await ComponentLicenseGate.EnsureAsync("capture.ffmpeg", token);
        var path = Path.Combine(VideoDirectory, "provenance.json");
        using var doc = JsonDocument.Parse(await File.ReadAllBytesAsync(path, token));
        foreach (var entry in doc.RootElement.GetProperty("Files").EnumerateArray())
        {
            var name = entry.GetProperty("Name").GetString() ?? throw new InvalidDataException();
            var file = Path.GetFullPath(Path.Combine(VideoDirectory, name));
            if (!file.StartsWith(Path.GetFullPath(VideoDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Invalid video runtime entry.");
            if (!string.Equals(await HashAsync(file, token),
                entry.GetProperty("Sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Video runtime checksum mismatch.");
        }
    }
    public static async Task<string> RunAsync(IEnumerable<string> arguments, CancellationToken token)
    {
        using var process = OwnedProcessRegistry.Shared.Start(Info(Path.Combine(VideoDirectory, "ffprobe.exe"), arguments), "Music.Video.Probe");
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None); await Task.WhenAll(output, errors);
            token.ThrowIfCancellationRequested(); throw new IOException("Video probe timed out.");
        }
        var text = await output; var error = await errors;
        if (process.ExitCode != 0 || text.Length > 8 * 1024 * 1024) throw new IOException("Video probe failed: " + error[^Math.Min(1000, error.Length)..]);
        return text;
    }
    public static async Task<string> HashAsync(string path, CancellationToken token)
    {
        using var stream = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
    }
}
