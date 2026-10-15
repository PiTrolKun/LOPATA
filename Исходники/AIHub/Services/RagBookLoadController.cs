using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AIHub.Services;

internal readonly record struct RagBookResources(double? CpuPercent, long TotalBytes, long AvailableBytes);

/// <summary>System-wide sampled load; never suspends or changes another application's process.</summary>
internal sealed class RagBookResourceProbe
{
    private ulong _idle, _total;
    public RagBookResources Read()
    {
        double? usage = null;
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            var total = kernel + user;
            if (_total > 0 && total > _total && idle >= _idle)
                usage = Math.Clamp(100d * (1 - (double)(idle - _idle) / (total - _total)), 0, 100);
            _idle = idle; _total = total;
        }
        var memory = new MemoryStatus();
        if (!GlobalMemoryStatusEx(memory)) return new(usage, 0, 0);
        return new(usage, checked((long)memory.Total), checked((long)memory.Available));
    }
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatus memory);
    [StructLayout(LayoutKind.Sequential)]
    private sealed class MemoryStatus
    {
        public uint Length = (uint)Marshal.SizeOf<MemoryStatus>(), Load;
        public ulong Total, Available, PageTotal, PageAvailable, VirtualTotal, VirtualAvailable, Extended;
    }
}

internal sealed class RagBookLoadPolicy(int processors, int target)
{
    internal const long PageReserve = 384L * 1024 * 1024;
    public int Maximum { get; } = Math.Clamp((int)Math.Floor(Math.Max(1, processors) * Math.Clamp(target, 10, 90) / 100d), 1, 32);
    public int Capacity { get; private set; } = 1;
    public double? AverageCpu { get; private set; }
    public void Update(RagBookResources resources)
    {
        var reserve = Math.Max(2L * 1024 * 1024 * 1024, resources.TotalBytes / 10);
        var memoryLimit = resources.TotalBytes <= 0 ? 1 : (int)Math.Clamp((resources.AvailableBytes - reserve) / PageReserve, 0, Maximum);
        if (resources.CpuPercent is { } cpu)
        {
            AverageCpu = AverageCpu is { } mean ? mean * .65 + cpu * .35 : cpu;
            // Stop admitting new pages above target. Existing pages finish and flush normally.
            if (AverageCpu > target + 3) Capacity = Math.Max(0, Capacity - 1);
            else if (AverageCpu < target - 5) Capacity = Math.Min(Maximum, Capacity + 1);
        }
        else Capacity = 1; // Missing telemetry falls back to serial work, including after a previous throttle.
        Capacity = Math.Min(Capacity, memoryLimit);
    }
}

/// <summary>Bounded admission, cancellable even when external work consumes the whole CPU budget.</summary>
internal sealed class RagBookLoadController
{
    private readonly object _gate = new();
    private readonly Func<RagBookResources> _read;
    private readonly RagBookLoadPolicy _policy;
    private readonly IProgress<RagBookProgress> _progress;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _nextSample;
    private int _active;
    public int Maximum => _policy.Maximum;
    public RagBookLoadController(int target, IProgress<RagBookProgress> progress, Func<RagBookResources>? read = null, int? processors = null)
    {
        _read = read ?? new RagBookResourceProbe().Read; _progress = progress;
        _policy = new(processors ?? Environment.ProcessorCount, target);
        _read(); // Establish the first CPU interval before admitting parallel pages.
    }
    public async Task<IDisposable> EnterAsync(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_clock.ElapsedMilliseconds >= _nextSample)
                {
                    _policy.Update(_read()); _nextSample = _clock.ElapsedMilliseconds + 1000;
                    _progress.Report(new("Load", _active, _policy.Capacity,
                        _policy.AverageCpu is { } cpu ? $"{cpu:0}%" : "?"));
                }
                if (_active < _policy.Capacity) { _active++; return new Lease(this); }
            }
            await Task.Delay(150, token);
        }
    }
    private sealed class Lease(RagBookLoadController owner) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                lock (owner._gate) owner._active--;
        }
    }
}
