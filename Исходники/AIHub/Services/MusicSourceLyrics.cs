using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AIHub.Services;

public static class MusicSourceLyrics
{
    public static readonly string[] Components = ["runtime.whisper.cpu", "model.whisper.small"];
    public static async Task PrepareAsync(bool download, IProgress<AIHub.Models.ComponentDownloadProgress>? progress, CancellationToken token)
    {
        await ComponentLicenseGate.EnsureAsync(["basic"], token); var manager = new ComponentManager();
        foreach (var id in Components) {
            if (download) await manager.DownloadAndInstallAsync(id, progress, token);
            try { _ = InstalledComponentArtifact.Resolve(manager.Verify(id)); }
            catch (IOException error) { throw new InvalidDataException("Music.Transform.RecognizerMissing", error); }
        }
        await MusicAudioRuntime.Default.PrepareAsync(token);
    }
    public static async Task<string> ReadEmbeddedAsync(string source, CancellationToken token)
    {
        await MusicAudioRuntime.Default.PrepareAsync(token);
        using var data = JsonDocument.Parse(await MusicAudioRuntime.Default.RunAsync(true, ["-v", "error", "-show_entries", "format_tags", "-of", "json", source], token));
        if (!data.RootElement.TryGetProperty("format", out var format) || !format.TryGetProperty("tags", out var tags)) return "";
        foreach (var tag in tags.EnumerateObject())
            if (tag.Name.Equals("lyrics", StringComparison.OrdinalIgnoreCase) || tag.Name.StartsWith("unsyncedlyrics", StringComparison.OrdinalIgnoreCase))
                return tag.Value.GetString() ?? "";
        return "";
    }
    public static async Task<string> RecognizeAsync(string source, CancellationToken token, double start = 0, double? end = null)
    {
        await PrepareAsync(false, null, token);
        var manager = new ComponentManager();
        string Artifact(string id) => InstalledComponentArtifact.Resolve(manager.Verify(id));
        var folder = Path.Combine(AppDataPaths.BaseDirectory, "Music", "Transcriptions", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var wave = Path.Combine(folder, "audio.wav"); var output = Path.Combine(folder, "words");
        await MusicAudioRuntime.Default.RunAsync(false, ["-nostdin", "-v", "error", "-n", "-ss", start.ToString("R", System.Globalization.CultureInfo.InvariantCulture), "-i", source, "-t", Math.Min(360, end is { } finish ? finish - start : 360).ToString("R", System.Globalization.CultureInfo.InvariantCulture), "-map", "0:a:0", "-map_metadata", "-1", "-c:a", "pcm_s16le", "-ar", "16000", "-ac", "1", "-f", "wav", wave], token);
        var info = new ProcessStartInfo(Artifact(Components[0])) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        MusicHardwareProbe.CleanEnvironment(info);
        foreach (var argument in new[] { "-m", Artifact(Components[1]), "-f", wave, "-otxt", "-of", output, "-l", "auto", "-ng", "-t", Math.Clamp(Environment.ProcessorCount / 2, 1, 8).ToString() }) info.ArgumentList.Add(argument);
        using var process = OwnedProcessRegistry.Shared.Start(info, "Music.Cover.Lyrics");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(30));
        try {
            await process.WaitForExitAsync(timeout.Token); await Task.WhenAll(stdout, stderr);
            if (process.ExitCode != 0 || !File.Exists(output + ".txt")) throw new IOException("Music.Transform.RecognitionFailed");
            return await File.ReadAllTextAsync(output + ".txt", token);
        } finally {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None); await Task.WhenAll(stdout, stderr);
            if (File.Exists(wave)) File.Delete(wave);
        }
    }
}
