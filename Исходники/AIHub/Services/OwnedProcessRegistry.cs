using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace AIHub.Services;

/// <summary>Owns only processes started here; never adopts or kills by executable name.</summary>
public sealed class OwnedProcessRegistry : IDisposable
{
    public static OwnedProcessRegistry Shared { get; } = new();
    private readonly object _gate = new();
    private readonly Dictionary<int, Entry> _entries = [];
    private bool _disposed;
    public static string LogDirectory => Path.Combine(AppDataPaths.BaseDirectory, "Diagnostics", "Processes");
    private static readonly object LogGate = new();
    private sealed record Entry(Process Monitor, WindowsProcessJob Job, string Component, DateTime Started);
    public sealed record Snapshot(int Pid, string Component, DateTime Started, long MemoryBytes);

    public Process Start(ProcessStartInfo info, string component)
    {
        var process = new Process { StartInfo = info };
        try { Start(process, component); return process; }
        catch { process.Dispose(); throw; }
    }

    public bool Start(Process process, string component)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (process.StartInfo.UseShellExecute) throw new ArgumentException("External shell launches cannot be owned workers.");
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
            var job = new WindowsProcessJob();
            try
            {
                if (!process.Start()) throw new InvalidOperationException("Worker did not start.");
                // Attach immediately, before readiness checks, output pumps or user callbacks.
                // Process.Start cannot atomically assign a Job; native console launches below use suspension.
                try { job.Assign(process); }
                catch (Win32Exception) when (process.HasExited) { job.Dispose(); return true; }
                Register(process, job, component);
                return true;
            }
            catch (Exception ex)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception stop) when (stop is InvalidOperationException or Win32Exception) { }
                job.Dispose();
                Log("start_failed", component, null, ex.GetType().Name);
                throw;
            }
        }
    }

    public Process StartConsole(ProcessStartInfo info, string component, string logPath)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var job = new WindowsProcessJob();
            try
            {
                var process = WindowsConsoleProcess.Start(info, job, logPath);
                try { Register(process, job, component); return process; }
                catch { job.Dispose(); process.Dispose(); throw; }
            }
            catch { job.Dispose(); throw; }
        }
    }

    private void Register(Process process, WindowsProcessJob job, string component)
    {
        if (process.HasExited) { Log("exited", component, process.Id, process.ExitCode.ToString()); job.Dispose(); return; }
        Process monitor;
        try { monitor = Process.GetProcessById(process.Id); }
        catch (ArgumentException) when (process.HasExited) { job.Dispose(); Log("exited", component, process.Id); return; }
        try
        {
            _ = monitor.SafeHandle; // Pin identity now; PID reuse must never redirect cleanup.
            _entries.Add(process.Id, new(monitor, job, component, process.StartTime.ToUniversalTime()));
        }
        catch (Exception ex) when ((ex is InvalidOperationException or Win32Exception) && process.HasExited)
        { monitor.Dispose(); job.Dispose(); Log("exited", component, process.Id); return; }
        catch { monitor.Dispose(); throw; }
        Log("started", component, process.Id);
        _ = ObserveExitAsync(process.Id, monitor);
    }

    private async Task ObserveExitAsync(int pid, Process monitor)
    {
        // Never execute exit callbacks synchronously inside Register.
        await Task.Yield();
        try { await monitor.WaitForExitAsync().ConfigureAwait(false); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
        lock (_gate)
        {
            if (!_entries.Remove(pid, out var entry)) return;
            int? exit = null;
            try { exit = monitor.ExitCode; } catch (InvalidOperationException) { }
            Log("exited", entry.Component, pid, exit?.ToString());
            entry.Job.Dispose(); // Also retire surviving grandchildren of a finished worker.
            monitor.Dispose();
        }
    }

    public IReadOnlyList<Snapshot> GetSnapshot()
    {
        lock (_gate)
        {
            var result = new List<Snapshot>();
            foreach (var (pid, entry) in _entries)
            {
                try { entry.Monitor.Refresh(); if (!entry.Monitor.HasExited) result.Add(new(pid, entry.Component, entry.Started, entry.Monitor.WorkingSet64)); }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
            }
            return result;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var (pid, entry) in _entries)
            {
                Log("owner_shutdown", entry.Component, pid);
                entry.Job.Dispose();
                // ObserveExit owns disposal of the monitor after its wait completes.
            }
        }
    }

    internal static void Log(string kind, string component, int? pid = null, string? detail = null)
    {
        try
        {
            lock (LogGate)
            {
                Directory.CreateDirectory(LogDirectory);
                var path = Path.Combine(LogDirectory, "processes.jsonl");
                if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024)
                    File.Move(path, path + ".previous", true);
                File.AppendAllText(path, JsonSerializer.Serialize(new { at = DateTime.UtcNow, owner = Environment.ProcessId, kind, component, pid, detail }) + "\n");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
