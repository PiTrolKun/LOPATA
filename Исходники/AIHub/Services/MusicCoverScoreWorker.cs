using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace AIHub.Services;

public interface IMusicCoverScoreWorker
{
    Task<string> TranscribeAsync(string modelsRoot, string audio, bool harmony, Action<string> log, CancellationToken token);
}

/// <summary>Existing pinned native engine /transcribe; private loopback server with no Studio downloads.</summary>
public sealed class MusicCoverScoreWorker : IMusicCoverScoreWorker
{
    public async Task<string> TranscribeAsync(string modelsRoot, string audio, bool harmony, Action<string> log, CancellationToken token)
    {
        using var auditOperation = ComponentAudit.CaptureOperation();
        using var auditLog = ComponentAudit.CaptureLog(log);
        await MusicCoverScoreCatalog.PrepareAsync(false, null, token);
        var profile = MusicStudioRuntime.Current;
        await ComponentLicenseGate.EnsureAsync([MusicStudioRuntime.CurrentComponentId], token);
        await MusicStudioRuntime.VerifyAsync(profile, token); await MusicStudioRuntime.VerifyModelsAsync(modelsRoot, token);
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var executable = Path.Combine(profile.DirectoryPath, "engine", "yue-server.exe");
        var info = new ProcessStartInfo(executable) { WorkingDirectory = Path.GetDirectoryName(executable)!, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        MusicHardwareProbe.CleanEnvironment(info);
        foreach (var part in new[] { "--model", MusicModelVariants.Artifact(modelsRoot, MusicStudioRuntime.Variation, MusicComponentCatalog.ModelId),
            "--vae", MusicModelVariants.Artifact(modelsRoot, MusicStudioRuntime.Variation, MusicComponentCatalog.DecoderId), "--companion",
            MusicModelVariants.Artifact(modelsRoot, MusicStudioRuntime.Variation, MusicStudioRuntime.CompanionId), "--transcriber",
            Path.Combine(MusicCoverScoreCatalog.CacheDirectory, "SheetSage2-Q8_0.gguf"), "--host", IPAddress.Loopback.ToString(), "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--max-batch", "1", "--max-seq", "24576" }) info.ArgumentList.Add(part);
        token.ThrowIfCancellationRequested(); using var process = OwnedProcessRegistry.Shared.Start(info, "Music.Cover.Score");
        async Task Drain(StreamReader reader) { while (await reader.ReadLineAsync() is { } line) log("[Cover score] " + line[..Math.Min(line.Length, 4096)]); }
        var stdout = Drain(process.StandardOutput); var stderr = Drain(process.StandardError);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(45));
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) {
            BaseAddress = new UriBuilder("http", IPAddress.Loopback.ToString(), port).Uri, Timeout = TimeSpan.FromSeconds(20) };
        try {
            var startup = Stopwatch.StartNew();
            while (true) {
                timeout.Token.ThrowIfCancellationRequested(); if (process.HasExited) throw new IOException("Native score engine exited: " + process.ExitCode);
                try { using var health = await http.GetAsync("health", timeout.Token); if (health.IsSuccessStatusCode) break; } catch (HttpRequestException) { }
                if (startup.Elapsed > TimeSpan.FromMinutes(3)) throw new TimeoutException("Music.Transform.ScoreTimeout");
                await Task.Delay(250, timeout.Token);
            }
            using var form = new MultipartFormDataContent(); using var stream = File.OpenRead(audio);
            form.Add(new StreamContent(stream), "audio", "source.wav");
            // Native server interprets presence, not the value, of melody_only.
            if (!harmony) form.Add(new StringContent("true"), "melody_only");
            using var submitted = await http.PostAsync("transcribe", form, timeout.Token); submitted.EnsureSuccessStatusCode();
            using var initial = JsonDocument.Parse(await submitted.Content.ReadAsStringAsync(timeout.Token));
            var id = initial.RootElement.GetProperty("id").GetString()!;
            if (id.Length > 128 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) throw new InvalidDataException("Invalid native job id.");
            while (true) {
                timeout.Token.ThrowIfCancellationRequested(); if (process.HasExited) throw new IOException("Native score engine exited.");
                using var status = await http.GetAsync("job?id=" + id, timeout.Token); status.EnsureSuccessStatusCode();
                using var state = JsonDocument.Parse(await status.Content.ReadAsStringAsync(timeout.Token));
                var phase = state.RootElement.GetProperty("status").GetString();
                if (phase == "done") {
                    using var result = await http.GetAsync("job?id=" + id + "&result=1", timeout.Token); result.EnsureSuccessStatusCode();
                    using var body = JsonDocument.Parse(await result.Content.ReadAsStringAsync(timeout.Token));
                    var abc = body.RootElement.GetProperty("abc").GetString();
                    if (string.IsNullOrWhiteSpace(abc) || abc.Length > 1000000 || !abc.Contains("K:")) throw new InvalidDataException("Music.Transform.InvalidScore");
                    return abc;
                }
                if (phase is "failed" or "cancelled") throw new InvalidDataException("Music.Transform.ScoreFailed");
                await Task.Delay(500, timeout.Token);
            }
        }
        finally {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None); await Task.WhenAll(stdout, stderr);
        }
    }
}
