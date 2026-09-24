using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AIHub.Services;

/// <summary>Small startup-only evidence, also available when full transcript logging is disabled.</summary>
public sealed class LiteraryStartupDiagnostics(Action<string> log, Action<string, object?> detailed)
{
    private readonly string _id = Guid.NewGuid().ToString("N");
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _gate = new();
    private readonly Queue<object> _tail = new();
    private string _stage = "begin";
    private bool _finished;

    public static string Limit(string text) => text.Length <= 8192 ? text : text[..8192] + " [truncated]";

    public void Stage(string stage)
    {
        _stage = stage;
        Record("stage", new { stage });
    }

    public void EnvironmentInfo() => Record("environment", new
    {
        appVersion = typeof(LiteraryStartupDiagnostics).Assembly.GetName().Version?.ToString(),
        os = Environment.OSVersion.ToString(), runtime = RuntimeInformation.FrameworkDescription,
        architecture = RuntimeInformation.ProcessArchitecture.ToString(), processors = Environment.ProcessorCount,
        culture = CultureInfo.CurrentCulture.Name, acp = GetACP(), oemcp = GetOEMCP(),
        detailedLogging = LiteraryRequestDiagnostics.Enabled
    });

    public void Capture(string stream, string line)
    {
        lock (_gate)
        {
            if (_finished) return;
            if (_tail.Count == 40) _tail.Dequeue();
            _tail.Enqueue(new { stream, text = line.Length <= 2048 ? line : line[..2048] + " [truncated]" });
        }
    }

    public void Ready(Process process, int capacity)
    {
        lock (_gate) { _finished = true; _tail.Clear(); }
        Record("ready", new { process = Snapshot(process), capacity });
    }

    public void Failure(Exception error, Process? process, bool cancelled, bool timedOut)
    {
        object[] tail;
        lock (_gate) { _finished = true; tail = _tail.ToArray(); _tail.Clear(); }
        var state = Snapshot(process);
        Record("failure", new
        {
            reason = cancelled ? "cancelled" : timedOut ? "startup_timeout" : state?.HasExited == true ? "process_exited" : "error",
            stage = _stage, process = state, exception = error.ToString(),
            nativeErrorCode = (error as Win32Exception)?.NativeErrorCode, error.HResult, tail
        });
    }

    public void Record(string kind, object? data)
    {
        // Diagnostic sink failures must not replace the original startup outcome.
        try
        {
            var entry = new { attempt = _id, elapsedMs = _clock.Elapsed.TotalMilliseconds, stage = _stage, data };
            try { log("startup_" + kind + ": " + JsonSerializer.Serialize(entry)); }
            catch (Exception ex) when (Recoverable(ex)) { }
            try { detailed("startup_" + kind, entry); }
            catch (Exception ex) when (Recoverable(ex)) { }
        }
        catch (Exception ex) when (Recoverable(ex)) { }
    }

    private sealed record ProcessState(int? Pid, bool? HasExited, int? ExitCode, string? Unavailable = null);
    private static ProcessState? Snapshot(Process? process)
    {
        if (process is null) return null;
        try
        {
            var exited = process.HasExited;
            return new(process.Id, exited, exited ? process.ExitCode : null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        { return new(null, null, null, ex.GetType().Name); }
    }

    private static bool Recoverable(Exception ex) => ex is System.IO.IOException or UnauthorizedAccessException
        or InvalidOperationException or ArgumentException or NotSupportedException or JsonException or Win32Exception;

    [DllImport("kernel32.dll")] private static extern uint GetACP();
    [DllImport("kernel32.dll")] private static extern uint GetOEMCP();
}
