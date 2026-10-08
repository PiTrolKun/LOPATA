using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace AIHub.Services;

/// <summary>One private server and its engine tree, never an existing user's Studio.</summary>
internal sealed class MusicStudioLease : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task _stdout, _stderr;
    private readonly Action<string> _log;
    private readonly HttpClient _http = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly string _data;
    public Uri Address { get; }
    private MusicStudioLease(Process process, Uri address, string data, Action<string> log)
    {
        _process = process; Address = address; _data = data; _log = log;
        _stdout = DrainAsync(process.StandardOutput); _stderr = DrainAsync(process.StandardError);
    }
    public static async Task<MusicStudioLease> StartAsync(string modelsRoot, string data, int context, Action<string> log, CancellationToken token)
    {
        Directory.CreateDirectory(data);
        // Reserve both ephemeral loopback ports together. No fixed port and no discovery/adoption of another server.
        var front = new TcpListener(IPAddress.Loopback, 0); var engine = new TcpListener(IPAddress.Loopback, 0);
        front.Start(); engine.Start();
        var frontPort = ((IPEndPoint)front.LocalEndpoint).Port; var enginePort = ((IPEndPoint)engine.LocalEndpoint).Port;
        front.Stop(); engine.Stop();
        var settings = Path.Combine(data, "studio-settings.json");
        await File.WriteAllTextAsync(settings, JsonSerializer.Serialize(new {
            engine_options = new { backend = "auto", keep_loaded = false, max_batch = 1, max_seq = context },
            lyrics_sync = new { enabled = false, provider = "none" }, cover_auto = false,
            configuration = new { selections = new[] { new { capability = "music_generation", mode = "local", local_engine = "yue2-cpp" } } }
        }), token);
        var info = new ProcessStartInfo(Path.Combine(MusicStudioRuntime.DirectoryPath, "music-server.exe")) {
            WorkingDirectory = MusicStudioRuntime.DirectoryPath, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        MusicHardwareProbe.CleanEnvironment(info);
        var values = new Dictionary<string, string> {
            ["YUE_STUDIO_PORT"] = frontPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["YUE_ENGINE_PORT"] = enginePort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["YUE_ENGINE_HOST"] = IPAddress.Loopback.ToString(),
            ["YUE_ENGINE_BASE_URL"] = new UriBuilder("http", IPAddress.Loopback.ToString(), enginePort).Uri.AbsoluteUri.TrimEnd('/'),
            ["YUE_ENGINE_ROOT"] = Path.Combine(MusicStudioRuntime.DirectoryPath, "engine"),
            ["YUE_STUDIO_DATA_ROOT"] = data, ["YUE_STUDIO_SETTINGS_PATH"] = settings,
            ["YUE_MODELS_ROOT"] = Path.Combine(data, "model-state"),
            ["LOPATA_YUE_BACKBONE"] = MusicModelVariants.Artifact(modelsRoot, MusicStudioRuntime.Variation, MusicComponentCatalog.ModelId),
            ["LOPATA_YUE_VAE"] = MusicModelVariants.Artifact(modelsRoot, MusicStudioRuntime.Variation, MusicComponentCatalog.DecoderId),
            ["LOPATA_YUE_COMPANION"] = MusicModelVariants.Artifact(modelsRoot, MusicStudioRuntime.Variation, MusicStudioRuntime.CompanionId)
        };
        foreach (var pair in values) info.Environment[pair.Key] = pair.Value;
        var owned = OwnedProcessRegistry.Shared.Start(info, "Music.Studio");
        var lease = new MusicStudioLease(owned, new UriBuilder("http", IPAddress.Loopback.ToString(), frontPort).Uri, data, log);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromMinutes(3));
            using var engineHttp = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(2) };
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested(); lease.EnsureRunning();
                await lease.ReadLogsAsync(deadline.Token);
                try {
                    using var result = await engineHttp.GetAsync(values["YUE_ENGINE_BASE_URL"] + "/health", deadline.Token);
                    using var service = await engineHttp.GetAsync(new Uri(lease.Address, "health"), deadline.Token);
                    if (result.IsSuccessStatusCode && service.IsSuccessStatusCode) {
                        using var health = JsonDocument.Parse(await service.Content.ReadAsStringAsync(deadline.Token));
                        var executable = health.RootElement.GetProperty("service_executable").GetString();
                        if (!string.Equals(executable, info.FileName, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Studio port belongs to another executable.");
                        break;
                    }
                } catch (HttpRequestException) { }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
                await Task.Delay(500, deadline.Token);
            }
            log("[Studio] engine ready; revision=" + MusicStudioRuntime.Revision + "; companion=" + MusicStudioRuntime.CompanionRevision);
            return lease;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) {
            await lease.DisposeAsync(); throw new TimeoutException("Studio engine startup exceeded three minutes.");
        }
        catch { await lease.DisposeAsync(); throw; }
    }
    public async Task<JsonElement> SendAsync(string route, object? body, CancellationToken token)
    {
        EnsureRunning();
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, new Uri(Address, route));
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, token);
        var text = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Studio {route}: {(int)response.StatusCode}; {text[..Math.Min(text.Length, 4096)]}");
        using var json = JsonDocument.Parse(text); return json.RootElement.Clone();
    }
    public async Task CopyAudioAsync(string route, string path, CancellationToken token)
    {
        if (!route.StartsWith("/v1/library/media/", StringComparison.Ordinal) || route.Contains("..") || route.Contains('?'))
            throw new InvalidDataException("Unexpected Studio audio URL.");
        using var response = await _http.GetAsync(new Uri(Address, route), HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await response.Content.CopyToAsync(output, token); output.Flush(true);
    }
    public async Task ReadLogsAsync(CancellationToken token)
    {
        // Includes the loader's startup lines before the engine's own /logs comes up.
        try {
            var path = Path.Combine(_data, "engine.log");
            if (File.Exists(path)) {
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(input, Encoding.UTF8);
                while (await reader.ReadLineAsync(token) is { } line) if (_seen.Add(line)) _log(line);
            }
        } catch (IOException) { }
    }
    private void EnsureRunning()
    { if (_process.HasExited) throw new InvalidOperationException("Studio exited: " + _process.ExitCode); }
    private async Task DrainAsync(StreamReader reader)
    { while (await reader.ReadLineAsync() is { } line) _log("[Studio] " + line[..Math.Min(line.Length, 4096)]); }
    public async ValueTask DisposeAsync()
    {
        try {
            if (!_process.HasExited) {
                await OwnedProcessRegistry.Shared.StopAndWaitAsync(_process.Id);
                await _process.WaitForExitAsync(CancellationToken.None);
            }
            // The registry's Windows Job owns descendants and closes them on exit.
            await Task.WhenAll(_stdout, _stderr);
        } finally { _process.Dispose(); _http.Dispose(); }
        // Private server media are temporary duplicates; the stage output and receipts live outside this folder.
        var root = Path.GetFullPath(_data) + Path.DirectorySeparatorChar;
        foreach (var path in Directory.EnumerateFiles(_data, "*", SearchOption.AllDirectories)
            .Where(p => Path.GetExtension(p) is ".wav" or ".mp3" or ".flac" or ".opus")) {
            if (!Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(path); } catch (IOException error) { _log("[Studio] temporary media cleanup: " + error.Message); }
        }
    }
}
