using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace AIHub.Services;

public sealed class LiteraryJellyWorkerException(string message, bool outOfMemory = false) : IOException(message)
{ public bool OutOfMemory { get; } = outOfMemory; }

internal sealed class LiteraryJellyWorker : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task _stderr;
    private readonly Action<string, object> _log;
    private readonly System.Text.StringBuilder _errors = new();
    private bool _usesCpu;
    public int Id => _process.Id;
    internal static async Task<LiteraryJellyWorker> CreateAsync(string mode, string model, string dependencies,
        Action<string, object> log, string device, CancellationToken token)
    {
        var runtime = await ManagedPythonRuntime.ResolveAsync(device, token);
        log("python_profile", new { runtime.Entry.Id, runtime.Device, runtime.FreeBytes, runtime.Reason });
        return new(mode, model, dependencies, log, runtime.Device, runtime.Python);
    }

    internal LiteraryJellyWorker(string mode, string model, string dependencies, Action<string, object> log,
        string device, string python)
    {
        _log = log;
        var info = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(python))!, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8, StandardInputEncoding = new System.Text.UTF8Encoding(false) };
        if (!System.Text.RegularExpressions.Regex.IsMatch(device, @"\A(auto|cpu|(cuda|xpu):[0-9]{1,2})\z"))
            throw new ArgumentException("Invalid memory executor device policy.", nameof(device));
        foreach (var key in info.Environment.Keys.Where(key => key.StartsWith("PYTHON", StringComparison.OrdinalIgnoreCase)).ToArray())
            info.Environment.Remove(key);
        ManagedPythonLaunch.ScriptArguments(info, [LiteraryJellyInstallation.Script, "--mode", mode, "--model", model, "--deps", dependencies, "--device", device]);
        info.Environment["PYTHONUTF8"] = "1"; info.Environment["PYTHONUNBUFFERED"] = "1";
        _process = OwnedProcessRegistry.Shared.Start(info, "Literary Jelly " + mode);
        _log("worker_launch", new { mode, model, dependencies, pid = Id, arguments = info.ArgumentList.ToArray() });
        _stderr = PumpErrorsAsync();
    }
    private async Task PumpErrorsAsync()
    {
        while (await _process.StandardError.ReadLineAsync() is { } line)
        {
            lock (_errors) { if (_errors.Length > 8000) _errors.Remove(0, _errors.Length - 4000); _errors.AppendLine(line); }
            _log("worker_stderr", line);
        }
    }
    public async Task<JsonElement> ReadAsync(CancellationToken ct)
    {
        var line = await _process.StandardOutput.ReadLineAsync(ct);
        if (line is null)
        {
            await _stderr; string error; lock (_errors) error = _errors.ToString();
            throw new LiteraryJellyWorkerException("Extractor exited without a result: " + error);
        }
        using var doc = JsonDocument.Parse(line); var result = doc.RootElement.Clone();
        if (result.TryGetProperty("backend", out var backend)) _usesCpu = backend.GetString() == "cpu";
        _log("worker_response", result);
        if (result.GetProperty("type").GetString() == "error")
            throw new LiteraryJellyWorkerException(result.GetProperty("error").GetString()!, result.TryGetProperty("oom", out var oom) && oom.GetBoolean());
        return result;
    }
    public async Task<JsonElement> CallAsync(object request, CancellationToken ct)
    {
        _log("worker_request", request);
        await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), ct);
        await _process.StandardInput.FlushAsync(ct);
        return await ReadAsync(ct);
    }
    public async Task<string> ExtractAsync(string text, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(_usesCpu ? 15 : 3));
        var result = await CallAsync(new { action = "extract", text }, timeout.Token);
        if (result.GetProperty("type").GetString() != "result") throw new InvalidDataException("Unexpected extractor response.");
        return result.GetProperty("output").GetRawText();
    }
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited) _process.Kill(true);
            await _process.WaitForExitAsync(); await _stderr;
            _log("worker_exit", new { pid = Id, code = _process.ExitCode });
        }
        finally { _process.Dispose(); }
    }
}
