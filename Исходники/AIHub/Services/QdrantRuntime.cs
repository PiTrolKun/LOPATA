using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIHub.Services;

public sealed class QdrantRuntime(QdrantOptions options, OwnedProcessRegistry? registry = null)
{
    public static QdrantRuntime Shared { get; } = new(new());
    private readonly OwnedProcessRegistry _registry = registry ?? OwnedProcessRegistry.Shared;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private Process? _process;
    private HttpClient? _http;
    private FileStream? _lease;
    private int _port;
    private string? _activeProbe;
    public string State { get; private set; } = "Stopped";
    public QdrantOptions Options => options;
    public int? Pid { get { try { return _process is { HasExited: false } ? _process.Id : null; } catch (InvalidOperationException) { return null; } } }
    public bool LastStopGraceful { get; private set; }
    public bool IsReady => State == "Ready" && Pid is not null;

    public async Task StartAsync(CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        await ComponentLicenseGate.EnsureAsync(QdrantOptions.LicenseId, linked.Token);
        await _gate.WaitAsync(linked.Token);
        try { await StartCoreAsync(linked.Token).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
    private async Task StartCoreAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (IsReady) return;
        await StopCoreAsync().ConfigureAwait(false);
        State = "Starting";
        try
        {
            if (!await QdrantInstaller.IsInstalledAsync(options, token).ConfigureAwait(false))
                throw new FileNotFoundException("Install and verify Qdrant first.");
            Directory.CreateDirectory(options.DataDirectory);
            _lease = new FileStream(Path.Combine(options.DataDirectory, ".owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(); _port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            var apiKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var configPath = Path.Combine(options.DataDirectory, "runtime.yaml");
            await File.WriteAllTextAsync(configPath, Configuration(options, _port), token).ConfigureAwait(false);
            if (File.Exists(options.LogPath)) File.Move(options.LogPath, options.LogPath + ".previous", true);
            var info = new ProcessStartInfo(options.Executable) { WorkingDirectory = options.DataDirectory, UseShellExecute = false };
            foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("QDRANT", StringComparison.OrdinalIgnoreCase) || k == "RUN_MODE").ToArray()) info.Environment.Remove(key);
            info.Environment["QDRANT__SERVICE__API_KEY"] = apiKey;
            info.ArgumentList.Add("--config-path"); info.ArgumentList.Add(configPath);
            _process = _registry.StartConsole(info, "Qdrant", options.LogPath);
            _http = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri($"http://{IPAddress.Loopback}:{_port}/"), Timeout = TimeSpan.FromSeconds(15) };
            _http.DefaultRequestHeaders.Add("api-key", apiKey);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(options.StartupTimeout);
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (_process.HasExited) throw new IOException("Qdrant exited during startup. See its log.");
                try
                {
                    using var health = await _http.GetAsync("readyz", timeout.Token).ConfigureAwait(false);
                    if (health.IsSuccessStatusCode) break;
                }
                catch (HttpRequestException) { }
                await Task.Delay(150, timeout.Token).ConfigureAwait(false);
            }
            if (_process.HasExited) throw new IOException("Qdrant exited before readiness confirmation.");
            // readyz is intentionally public upstream; confirm an authenticated API before trusting the endpoint.
            using var authenticated = await RequestAsync(HttpMethod.Get, "collections", null, timeout.Token).ConfigureAwait(false);
            State = "Ready";
            await CleanupPendingProbesAsync(timeout.Token).ConfigureAwait(false);
            OwnedProcessRegistry.Log("ready", "Qdrant", Pid);
        }
        catch
        {
            await StopCoreAsync().ConfigureAwait(false);
            State = "Failed";
            throw;
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
    public async Task ShutdownAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await StopAsync().ConfigureAwait(false);
    }
    private async Task StopCoreAsync()
    {
        var process = _process;
        _process = null;
        LastStopGraceful = process is null;
        try
        {
            if (process is not null && !process.HasExited)
            {
                State = "Stopping";
                if (File.Exists(options.HelperExecutable))
                {
                    var signal = new ProcessStartInfo(options.HelperExecutable) { UseShellExecute = false };
                    foreach (var arg in new[] { "--owned-console-stop", process.Id.ToString(), process.StartTime.ToUniversalTime().Ticks.ToString() }) signal.ArgumentList.Add(arg);
                    using var helper = _registry.Start(signal, "Qdrant shutdown signal");
                    using var timeout = new CancellationTokenSource(options.StopTimeout);
                    try { await helper.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
                    finally { if (!helper.HasExited) helper.Kill(entireProcessTree: true); }
                    if (helper.ExitCode != 0) throw new IOException("Console shutdown signal failed.");
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                    LastStopGraceful = process.ExitCode == 0;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException)
        { OwnedProcessRegistry.Log("graceful_stop_failed", "Qdrant", detail: ex.GetType().Name); }
        finally
        {
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        using var deadline = new CancellationTokenSource(options.StopTimeout);
                        await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                    }
                }
                catch { _process = process; State = "Failed"; throw; } // Keep the data lease until exit is confirmed.
                OwnedProcessRegistry.Log(LastStopGraceful ? "stopped" : "forced_stop", "Qdrant", process.Id);
                process.Dispose();
            }
            _http?.Dispose(); _http = null;
            _lease?.Dispose(); _lease = null;
            State = "Stopped";
        }
    }

    internal static string Configuration(QdrantOptions options, int port) => $"""
        log_level: INFO
        telemetry_disabled: true
        storage:
          storage_path: {JsonSerializer.Serialize(Path.Combine(options.DataDirectory, "storage"))}
          snapshots_path: {JsonSerializer.Serialize(Path.Combine(options.DataDirectory, "snapshots"))}
          performance:
            max_search_threads: 2
            optimizer_cpu_budget: 2
        service:
          host: {IPAddress.Loopback}
          http_port: {port}
          grpc_port: null
          enable_cors: false
          max_workers: 2
        cluster:
          enabled: false
        """;

    public sealed record ProbeResult(bool SearchPassed, bool RestartReadPassed, bool GracefulRestart, long MemoryBytes);
    public async Task<ProbeResult> ProbeAsync(CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        token = linked.Token;
        await ComponentLicenseGate.EnsureAsync(QdrantOptions.LicenseId, token);
        await _gate.WaitAsync(token);
        var collection = "lopata_probe_" + Guid.NewGuid().ToString("N");
        var created = false;
        _activeProbe = collection;
        try
        {
            await StartCoreAsync(token).ConfigureAwait(false);
            // Persist intent before HTTP: a cancelled response may still have created the collection.
            await File.WriteAllTextAsync(Path.Combine(options.DataDirectory, collection + ".pending"), "LOPATA probe", token);
            created = true;
            using var create = await RequestAsync(HttpMethod.Put, $"collections/{collection}", new { vectors = new { size = 3, distance = "Cosine" } }, token);
            using var put = await RequestAsync(HttpMethod.Put, $"collections/{collection}/points?wait=true", new { points = new[] { new { id = 1, vector = new[] { 1f, 0, 0 }, payload = new { text = "ЛОПАТА — техническая проверка памяти" } } } }, token);
            using var search = await RequestAsync(HttpMethod.Post, $"collections/{collection}/points/query", new { query = new[] { 1f, 0, 0 }, limit = 1, with_payload = true }, token);
            if (search.RootElement.GetProperty("result").GetProperty("points")[0].GetProperty("id").GetInt32() != 1) throw new InvalidDataException("Qdrant search verification failed.");
            await StopCoreAsync().ConfigureAwait(false);
            var graceful = LastStopGraceful;
            await StartCoreAsync(token).ConfigureAwait(false);
            using var read = await RequestAsync(HttpMethod.Get, $"collections/{collection}/points/1", null, token);
            if (read.RootElement.GetProperty("result").GetProperty("payload").GetProperty("text").GetString() != "ЛОПАТА — техническая проверка памяти") throw new InvalidDataException("Qdrant restart persistence verification failed.");
            _process!.Refresh();
            return new(true, true, graceful, _process.WorkingSet64);
        }
        finally
        {
            _activeProbe = null;
            try
            {
                if (created && IsReady)
                {
                    using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await CleanupPendingProbesAsync(cleanupTimeout.Token).ConfigureAwait(false);
                }
                else if (created) OwnedProcessRegistry.Log("probe_cleanup_pending", "Qdrant", detail: collection);
            }
            finally { _gate.Release(); }
        }
    }
    private async Task CleanupPendingProbesAsync(CancellationToken token)
    {
        foreach (var marker in Directory.EnumerateFiles(options.DataDirectory, "lopata_probe_*.pending"))
        {
            var name = Path.GetFileNameWithoutExtension(marker);
            if (name == _activeProbe || !Guid.TryParseExact(name["lopata_probe_".Length..], "N", out _)) continue;
            using var response = await _http!.DeleteAsync("collections/" + name, token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.NotFound) response.EnsureSuccessStatusCode();
            File.Delete(marker);
        }
    }
    private async Task<JsonDocument> RequestAsync(HttpMethod method, string path, object? body, CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await _http!.SendAsync(request, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
    }
}
