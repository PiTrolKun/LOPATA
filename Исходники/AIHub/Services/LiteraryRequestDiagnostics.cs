using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AIHub.Services;

/// <summary>Opt-in local evidence. Never changes generation or throws into the chat.</summary>
public sealed class LiteraryRequestDiagnostics : IDisposable
{
    private static volatile bool _globallyEnabled;
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
    public string FilePath { get; }

    public LiteraryRequestDiagnostics(string role, Action<string> report, string? root = null,
        Func<bool>? enabled = null, long limit = 128L * 1024 * 1024)
    {
        _enabled = enabled ?? (() => Enabled); _report = report; _limit = limit;
        FilePath = Path.Combine(root ?? Root, role, DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N") + ".jsonl");
        if (!_enabled()) { _closed = true; return; }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            _writer = new StreamWriter(new FileStream(FilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
            Write("begin", new { role, appVersion = typeof(LiteraryRequestDiagnostics).Assembly.GetName().Version?.ToString(),
                os = Environment.OSVersion.ToString(), processors = Environment.ProcessorCount,
                runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription });
            _report("Detailed diagnostics: " + FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Fail(ex); }
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
                if (_bytes + Encoding.UTF8.GetByteCount(line) > _limit)
                { Append("truncated", "Per-request diagnostic byte limit reached."); _report("Detailed diagnostics truncated: " + FilePath); Close(); return; }
                _writer.WriteLine(line); _bytes += Encoding.UTF8.GetByteCount(line) + 2;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Fail(ex); }
        }
    }

    public void Watch(Process process)
    {
        if (_closed) return;
        _gpu = new ProcessGpuDiagnostics();
        _timer = new System.Threading.Timer(_ =>
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
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            { Write("resources_unavailable", ex.Message); }
            }
        }, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    private void Append(string kind, string data) => _writer?.WriteLine(JsonSerializer.Serialize(new { utc = DateTime.UtcNow, elapsedMs = _clock.Elapsed.TotalMilliseconds, kind, data }));
    private void Close() { _closed = true; _writer?.Dispose(); _writer = null; }
    private void Fail(Exception ex) { _report("Detailed diagnostics I/O failure: " + ex.Message); try { Close(); } catch (IOException) { _closed = true; } }
    public void Dispose() { _timer?.Dispose(); lock (_gate) { _gpu?.Dispose(); Write("end", null); try { Close(); } catch (IOException ex) { _report(ex.Message); } } }
}
