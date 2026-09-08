using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using AIHub.Models;

namespace AIHub.Services;

public enum LiteraryChatProfile { WriterCpu, AdvisorGpu }

/// <summary>Independent text-only Alpha instance. Placement is explicit for each role.</summary>
public sealed class LiteraryChatRuntime : IDisposable
{
    private readonly LiteraryChatProfile _profile;
    private string Component => _profile == LiteraryChatProfile.WriterCpu ? "LiteraryWriterCPU" : "LiteraryAdvisorGPU";
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private int _port;
    private readonly object _logGate = new();
    private readonly string _logPath;
    public LiteraryChatRuntime(LiteraryChatProfile profile = LiteraryChatProfile.WriterCpu)
    {
        _profile = profile;
        var folder = Path.Combine(AppDataPaths.BaseDirectory, "Diagnostics", profile == LiteraryChatProfile.WriterCpu ? "LiteraryWriter" : "LiteraryAdvisor");
        Directory.CreateDirectory(folder);
        _logPath = Path.Combine(folder, DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N") + ".log");
    }
    public static string[] Arguments(string model, int port, LiteraryChatProfile profile = LiteraryChatProfile.WriterCpu) =>
    ["-m", model, "--host", IPAddress.Loopback.ToString(), "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "-c", OmniLlamaProtocol.ContextTokens.ToString(), "-np", "1", "-ngl", profile == LiteraryChatProfile.WriterCpu ? "0" : "99", "--device", profile == LiteraryChatProfile.WriterCpu ? "none" : "CUDA0",
        profile == LiteraryChatProfile.WriterCpu ? "--no-op-offload" : "--op-offload", "--fit", "off", "--cache-ram", "0", "--no-context-shift", "--offline", "--jinja", "--reasoning-format", "deepseek",
        "-t", Math.Clamp(Environment.ProcessorCount / 2, 1, 8).ToString(), "-tb", Math.Clamp(Environment.ProcessorCount / 2, 1, 8).ToString()];

    public async Task<string> SendAsync(IReadOnlyList<ImageAnalysisHiddenMessage> history,
        IProgress<ModelStreamChunk>? progress, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMinutes(15));
            var ct = deadline.Token;
            await PrepareAsync(ct);
            var server = new Uri($"http://{IPAddress.Loopback}:{_port}/");
            var count = await OmniLlamaContextProbe.MeasureAsync(_http, server, history, "", ct);
            var budget = Math.Min(8192, OmniContextBudget.OutputBudget(count, OmniLlamaProtocol.ContextTokens));
            Log($"request input={count}; outputBudget={budget}");
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(server, "v1/chat/completions"))
            { Content = new StringContent(OmniLlamaProtocol.BuildRequest(history, "", budget), Encoding.UTF8, "application/json") };
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var result = await OmniLlamaProtocol.ReadAsync(stream, progress, null, ct);
            Log(RuntimeResourceDiagnostics.DescribeSnapshot(Component, _process!, "answer"));
            return result.Content;
        }
        catch (Exception ex)
        {
            Log("request failed: " + ex.GetType().Name + ": " + ex.Message);
            Stop();
            throw;
        }
        finally { _gate.Release(); }
    }

    private async Task PrepareAsync(CancellationToken token)
    {
        if (_process is { HasExited: false }) return;
        Stop();
        var card = new ManagedModelLibraryStore().Load(ManagedModelCatalog.OmniAlphaArtifactId);
        if (card is null || card.Status != ManagedModelStatuses.Installed || card.Revision != ManagedModelCatalog.OmniAlphaRevision)
            throw new FileNotFoundException("Installed Alpha model is required.");
        var file = card.Files.Single(f => f.Purpose == "main_model");
        var root = Path.GetFullPath(card.InstallDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var model = Path.GetFullPath(Path.Combine(root, file.RelativePath));
        if (!model.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(model) || new FileInfo(model).Length != file.SizeBytes)
            throw new FileNotFoundException("Registered Alpha model file is missing or changed.");
        if (!File.Exists(LlamaBackendPaths.ServerExecutablePath)) throw new FileNotFoundException("Installed llama.cpp backend is required.");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); _port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var info = new ProcessStartInfo(LlamaBackendPaths.ServerExecutablePath)
        {
            WorkingDirectory = LlamaBackendPaths.DirectoryPath, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("LLAMA_ARG_", StringComparison.Ordinal)).ToArray()) info.Environment.Remove(key);
        foreach (var arg in Arguments(model, _port, _profile)) info.ArgumentList.Add(arg);
        var process = new Process { StartInfo = info };
        _process = process;
        process.OutputDataReceived += (_, e) => { if (e.Data is { } line) Log(line); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is { } line) Log(line); };
        token.ThrowIfCancellationRequested();
        if (!process.Start()) throw new InvalidOperationException("Writer runtime did not start.");
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        Log(RuntimeResourceDiagnostics.DescribeLaunch(Component, process, $"profile={_profile}; context=32768; no projector; threads<=8; cache-ram=0", model));
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
        startup.CancelAfter(TimeSpan.FromSeconds(90));
        while (true)
        {
            startup.Token.ThrowIfCancellationRequested();
            if (process.HasExited) throw new InvalidOperationException("Writer runtime exited: " + process.ExitCode);
            try
            {
                using var response = await _http.GetAsync($"http://{IPAddress.Loopback}:{_port}/health", startup.Token);
                if (response.IsSuccessStatusCode) break;
            }
            catch (HttpRequestException) { }
            await Task.Delay(250, startup.Token);
        }
        Log(RuntimeResourceDiagnostics.DescribeSnapshot(Component, process, "loaded"));
    }
    private void Log(string line)
    {
        try { lock (_logGate) File.AppendAllText(_logPath, DateTime.UtcNow.ToString("O") + " " + line + Environment.NewLine); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    public void Stop()
    {
        var process = Interlocked.Exchange(ref _process, null);
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        finally { process.Dispose(); }
    }
    public void Dispose() { Stop(); _http.Dispose(); }
}
