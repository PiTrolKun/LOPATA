using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AIHub.Services;

/// <summary>Local evidence. Recoverable diagnostic failures never interrupt generation.</summary>
public sealed class LiteraryRequestDiagnostics : IDisposable
{
    private static volatile bool _globallyEnabled = true;
    public static bool Enabled { get => _globallyEnabled; set => _globallyEnabled = value; }
    public static string Root => Path.Combine(AppDataPaths.BaseDirectory, "Diagnostics", "LiteraryDetailed");
    private readonly object _gate = new();
    private readonly Func<bool> _enabled;
    private readonly Action<string> _report;
    private readonly long _limit;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private StreamWriter? _writer;
    private bool _closed;
    private long _bytes;
    private System.Threading.Timer? _timer;
    private ProcessGpuDiagnostics? _gpu;
    public string FilePath { get; } = "";

    public LiteraryRequestDiagnostics(string role, Action<string> report, string? root = null,
        Func<bool>? enabled = null, long limit = 128L * 1024 * 1024)
    {
        _enabled = enabled ?? (() => Enabled); _report = report; _limit = limit;
        try
        {
            if (!_enabled()) { _closed = true; return; }
            FilePath = Path.Combine(root ?? Root, role, DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N") + ".jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            _writer = new StreamWriter(new FileStream(FilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
            Write("begin", new { role, appVersion = typeof(LiteraryRequestDiagnostics).Assembly.GetName().Version?.ToString(),
                os = Environment.OSVersion.ToString(), processors = Environment.ProcessorCount,
                runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription });
            if (!_closed) _report("Detailed diagnostics: " + FilePath);
        }
        catch (Exception ex) when (IsRecoverableDiagnosticFailure(ex)) { Fail(ex); }
        catch { Close(); throw; }
    }

    public void Write(string kind, object? data)
    {
        lock (_gate)
        {
            if (_closed || _writer is null) return;
            try
            {
                if (!_enabled()) { Append("disabled", "Detailed logging disabled; remaining request is not recorded."); Close(); return; }
                var line = JsonSerializer.Serialize(new { utc = DateTime.UtcNow, elapsedMs = _clock.Elapsed.TotalMilliseconds, kind, data });
                var bytes = Encoding.UTF8.GetByteCount(line) + Encoding.UTF8.GetByteCount(_writer.NewLine);
                if (_bytes + bytes > _limit)
                { Append("truncated", "Per-request diagnostic byte limit reached."); Close(); _report("Detailed diagnostics truncated: " + FilePath); return; }
                _writer.WriteLine(line); _bytes += bytes;
            }
            catch (Exception ex) when (IsRecoverableDiagnosticFailure(ex)) { Fail(ex); }
        }
    }

    public void Watch(Process process)
    {
        lock (_gate)
        {
            if (_closed || _timer is not null) return;
            try
            {
                if (!_enabled()) { Append("disabled", "Detailed logging disabled; remaining request is not recorded."); Close(); return; }
                _gpu = new ProcessGpuDiagnostics();
                _timer = new System.Threading.Timer(_ => Sample(process), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
            }
            catch (Exception ex) when (IsRecoverableDiagnosticFailure(ex)) { Fail(ex); }
        }
    }

    private void Sample(Process process)
    {
        lock (_gate)
        {
            if (_closed) return;
            try
            {
                process.Refresh();
                Write("resources", new { pid = process.Id, cpuTimeMs = process.TotalProcessorTime.TotalMilliseconds,
                    workingSetBytes = process.WorkingSet64, privateBytes = process.PrivateMemorySize64,
                    peakWorkingSetBytes = process.PeakWorkingSet64, virtualBytes = process.VirtualMemorySize64,
                    threads = process.Threads.Count, handles = process.HandleCount,
                    systemMemory = RuntimeResourceDiagnostics.DescribeSystemMemory("sample"),
                    gpu = _gpu?.Sample(process.Id) });
            }
            catch (Exception ex) when (IsRecoverableDiagnosticFailure(ex))
            { Write("resources_unavailable", ex.Message); }
        }
    }

    private void Append(string kind, string data)
    {
        if (_writer is null) return;
        var line = JsonSerializer.Serialize(new { utc = DateTime.UtcNow, elapsedMs = _clock.Elapsed.TotalMilliseconds, kind, data });
        var bytes = Encoding.UTF8.GetByteCount(line) + Encoding.UTF8.GetByteCount(_writer.NewLine);
        if (_bytes + bytes > _limit) return;
        _writer.WriteLine(line); _bytes += bytes;
    }

    private void Close()
    {
        _closed = true;
        var timer = _timer; _timer = null;
        var gpu = _gpu; _gpu = null;
        var writer = _writer; _writer = null;
        try { DisposeResource(timer); }
        finally
        {
            try { DisposeResource(gpu); }
            finally { DisposeResource(writer); }
        }
    }

    private void DisposeResource(IDisposable? resource)
    {
        try { resource?.Dispose(); }
        catch (Exception ex) when (IsRecoverableDiagnosticFailure(ex)) { ReportFailure(ex); }
    }

    private void Fail(Exception ex) { Close(); ReportFailure(ex); }

    private void ReportFailure(Exception error)
    {
        try { _report("Detailed diagnostics failure: " + error.GetType().Name + ": " + error.Message); }
        catch (Exception ex) when (IsRecoverableDiagnosticFailure(ex)) { }
    }

    // Payload getters and reporting callbacks are caller-owned and may throw any ordinary
    // exception. Contain them only at this optional diagnostics boundary; cancellation and
    // fatal runtime/native failures retain their normal semantics.
    private static bool IsRecoverableDiagnosticFailure(Exception ex) => ex is not
        (OperationCanceledException or OutOfMemoryException or StackOverflowException or
         AccessViolationException or System.Runtime.InteropServices.SEHException);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_closed) return;
            try { Write("end", null); }
            finally { Close(); }
        }
    }
}
