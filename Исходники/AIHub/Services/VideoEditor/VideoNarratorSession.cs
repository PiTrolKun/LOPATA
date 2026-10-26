using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AIHub.Services.VideoEditor;

/// <summary>One owned offline model process per operation; no retention after its owner is disposed.</summary>
internal sealed class VideoNarratorSession : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1);
    private readonly Queue<string> _tail = new();
    private Process? _process;
    private Task? _stderr;
    private StreamWriter? _log;
    private string? _key;
    private int _logged;
    private bool _disposed;
    internal bool IsRunning => _process is { HasExited: false };
    private static string Key(ManagedPythonSelection runtime, string overlay, string expanded, string modelKey) => string.Join('|', runtime.Python, runtime.Device, overlay, expanded, modelKey);
    internal bool IsFor(ManagedPythonSelection runtime, string overlay, string expanded, string modelKey) => IsRunning && _key == Key(runtime, overlay, expanded, modelKey);
    internal void Reset() => Stop();

    internal async Task RunAsync(ManagedPythonSelection runtime, string overlay, string expanded, string modelKey, string request, string output, string component, CancellationToken token, Action<double>? progress = null)
    {
        await _gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var key = Key(runtime, overlay, expanded, modelKey);
            if (!IsRunning || _key != key) Start(runtime, overlay, expanded, output, component, key);
            var process = _process!; var id = Guid.NewGuid().ToString("N");
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id, request, output }).AsMemory(), token);
            await process.StandardInput.FlushAsync(token);
            while (await process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false) is {} line)
            {
                const string prefix = "LOPATA_NARRATOR_RESPONSE ";
                const string progressPrefix = "LOPATA_NARRATOR_PROGRESS ";
                if (line.StartsWith(progressPrefix, StringComparison.Ordinal))
                {
                    using var update = JsonDocument.Parse(line[progressPrefix.Length..]);
                    var fraction = update.RootElement.GetProperty("fraction").GetDouble();
                    if (!double.IsFinite(fraction) || fraction < 0 || fraction > 1) throw new InvalidDataException("Invalid narrator progress.");
                    progress?.Invoke(fraction); continue;
                }
                if (!line.StartsWith(prefix, StringComparison.Ordinal)) { Append(line); continue; }
                using var response = JsonDocument.Parse(line[prefix.Length..]); var root = response.RootElement;
                if (root.GetProperty("id").GetString() != id) throw new IOException("Narrator returned an out-of-order response.");
                if (!root.GetProperty("ok").GetBoolean()) throw new IOException("Narrator failed: " + root.GetProperty("error").GetString() + "\n" + Tail());
                return;
            }
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            if (_stderr is not null) await _stderr.ConfigureAwait(false);
            throw new IOException("Narrator exited with code " + process.ExitCode + ": " + Tail());
        }
        catch { Stop(); throw; }
        finally { _gate.Release(); }
    }
    private void Start(ManagedPythonSelection runtime, string overlay, string expanded, string output, string component, string key)
    {
        Stop();
        var script = Path.Combine(AppContext.BaseDirectory, "Tools", "VideoNarrators", "worker.py");
        if (!File.Exists(script)) throw new FileNotFoundException("Missing video narration adapter.", script);
        var start = new ProcessStartInfo(runtime.Python) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false) };
        foreach (var name in new[] { "HF_HUB_OFFLINE", "TRANSFORMERS_OFFLINE", "HF_HUB_DISABLE_TELEMETRY" }) start.Environment[name] = "1";
        start.Environment["PYTHONNOUSERSITE"] = "1"; start.Environment["NUMBA_CACHE_DIR"] = Path.Combine(Path.GetDirectoryName(output)!, "numba-cache");
        ManagedPythonLaunch.ScriptArguments(start, [script, "--serve", overlay, expanded, runtime.Profile.Flavor(), runtime.Device]);
        _log = new StreamWriter(Path.Combine(Path.GetDirectoryName(output)!, "worker.log"), false, new UTF8Encoding(false)) { AutoFlush = true };
        _process = OwnedProcessRegistry.Shared.Start(start, component); _key = key;
        _stderr = DrainAsync(_process.StandardError);
    }
    private async Task DrainAsync(StreamReader stream)
    {
        while (await stream.ReadLineAsync().ConfigureAwait(false) is {} line) Append(line);
    }
    private void Append(string line)
    {
        lock (_tail)
        {
            if (_logged < 1024 * 1024) { _log?.WriteLine(line); _logged += line.Length + 1; }
            _tail.Enqueue(line.Length > 2000 ? line[..2000] : line); while (_tail.Count > 16) _tail.Dequeue();
        }
    }
    private string Tail() { lock (_tail) return string.Join('\n', _tail); }
    private void Stop()
    {
        var process = _process; _process = null; _key = null;
        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.StandardInput.WriteLine("{\"quit\":true}"); process.StandardInput.Flush();
                    if (!process.WaitForExit(1000)) { process.Kill(true); process.WaitForExit(5000); }
                }
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            { if (!process.HasExited) { process.Kill(true); process.WaitForExit(5000); } }
        }
        try { _stderr?.GetAwaiter().GetResult(); } catch (Exception error) when (error is IOException or ObjectDisposedException) { }
        process?.Dispose();
        _stderr = null;
        lock (_tail) { _log?.Dispose(); _log = null; _logged = 0; _tail.Clear(); }
    }
    public void Dispose() { _disposed = true; Stop(); }
}
