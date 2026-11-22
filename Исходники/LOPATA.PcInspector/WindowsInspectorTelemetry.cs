using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Lopata.Inspector;

namespace Lopata.PcInspector;

internal sealed record GpuConsumer(int Pid, string Adapter, long? DedicatedBytes, long? SharedBytes, double? UtilizationPercent);

internal sealed record TelemetryFrame(InspectorSample Sample, GpuConsumer[] Consumers)
{
    internal InspectorGpuSample? MatchAdapter(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested)) return null;
        var candidates = Sample.Gpus.Where(row =>
            row.Adapter.Equals(requested, StringComparison.OrdinalIgnoreCase) ||
            row.Name.Equals(requested, StringComparison.OrdinalIgnoreCase)).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    internal GpuConsumer? ResolveWorker(int processId)
    {
        var candidates = Consumers.Where(row => row.Pid == processId &&
            (row.DedicatedBytes > 0 || row.SharedBytes > 0 || row.UtilizationPercent > 0)).ToArray();
        if (candidates.Length == 1) return candidates[0];
        var active = candidates.Where(row => row.UtilizationPercent > 0).ToArray();
        return active.Length == 1 ? active[0] : null;
    }

    internal string? CompetingApplication(string adapter, HashSet<int> excludedPids, long minimumBytes = 768L * 1024 * 1024)
    {
        foreach (var consumer in Consumers.Where(row =>
                     row.DedicatedBytes >= minimumBytes &&
                     row.Adapter.Equals(adapter, StringComparison.OrdinalIgnoreCase) &&
                     !excludedPids.Contains(row.Pid)).OrderByDescending(row => row.DedicatedBytes))
        {
            try
            {
                using var process = Process.GetProcessById(consumer.Pid);
                if (!process.HasExited && process.StartTime.ToUniversalTime() <= Sample.TimestampUtc.UtcDateTime)
                    return process.ProcessName;
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception) { }
        }
        return null;
    }
}

internal sealed partial class WindowsInspectorTelemetry : IDisposable
{
    private const uint PdhMoreData = 0x800007d2;
    private const uint PdhDoubleNoCap = 0x8200;
    private nint _query;
    private readonly Dictionary<string, nint> _counters = [];
    private GpuIdentity[] _adapters = [];
    private DateTimeOffset _inventoryAt;
    private (ulong Idle, ulong Total)? _lastCpu;

    internal WindowsInspectorTelemetry()
    {
        if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) return;
        foreach (var (key, counter) in new[]
        {
            ("adapterDedicated", @"\GPU Adapter Memory(*)\Dedicated Usage"),
            ("adapterShared", @"\GPU Adapter Memory(*)\Shared Usage"),
            ("dedicated", @"\GPU Process Memory(*)\Dedicated Usage"),
            ("shared", @"\GPU Process Memory(*)\Shared Usage"),
            ("engine", @"\GPU Engine(*)\Utilization Percentage")
        })
            if (PdhAddEnglishCounter(_query, counter, 0, out var handle) == 0) _counters.Add(key, handle);
        _ = PdhCollectQueryData(_query);
    }

    internal TelemetryFrame Read(DateTimeOffset now)
    {
        if (now - _inventoryAt >= TimeSpan.FromMinutes(1))
        {
            _inventoryAt = now;
            try { _adapters = WindowsGpuInventory.Read(); }
            catch (Exception error) when (error is COMException or DllNotFoundException) { _adapters = []; }
        }

        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        var memoryValid = GlobalMemoryStatusEx(ref status);
        var cpu = ReadCpu();
        var valid = _query != 0 && PdhCollectQueryData(_query) == 0;
        if (!valid)
        {
            var unavailable = _adapters.Select(adapter =>
                new InspectorGpuSample(adapter.Adapter, adapter.Name, adapter.CapacityBytes, null, null, null)).ToArray();
            return new(new InspectorSample(now, memoryValid ? (long)status.AvailablePhysical : null,
                memoryValid ? (long)status.TotalPhysical : null, cpu, unavailable), []);
        }

        var dedicated = Aggregate("adapterDedicated", false);
        var shared = Aggregate("adapterShared", false);
        var processDedicated = Aggregate("dedicated", true);
        var processShared = Aggregate("shared", true);
        var hardwareEngines = new Dictionary<(string Adapter, string Engine), double>();
        var engines = Aggregate("engine", true, hardwareEngines);
        var adapters = _adapters.ToDictionary(item => item.Adapter, StringComparer.OrdinalIgnoreCase);
        var allIds = adapters.Keys.Concat(dedicated.Keys.Select(key => key.Adapter))
            .Concat(shared.Keys.Select(key => key.Adapter)).Distinct(StringComparer.OrdinalIgnoreCase);
        var gpus = allIds.Select(adapter =>
        {
            var id = adapters.GetValueOrDefault(adapter);
            double? load = hardwareEngines.Where(pair => pair.Key.Adapter == adapter)
                .Select(pair => (double?)Math.Min(100, pair.Value)).Max();
            return new InspectorGpuSample(adapter, id?.Name ?? adapter, id?.CapacityBytes,
                Bytes(dedicated, (0, adapter)), Bytes(shared, (0, adapter)), load);
        }).ToArray();
        var consumers = processDedicated.Keys.Concat(processShared.Keys).Concat(engines.Keys).Distinct()
            .Select(key => new GpuConsumer(key.Pid, key.Adapter, Bytes(processDedicated, key), Bytes(processShared, key),
                engines.TryGetValue(key, out var load) ? Math.Min(100, load) : null)).ToArray();
        return new(new InspectorSample(now, memoryValid ? (long)status.AvailablePhysical : null,
            memoryValid ? (long)status.TotalPhysical : null, cpu, gpus), consumers);
    }

    private static long? Bytes(Dictionary<(int Pid, string Adapter), double> values, (int, string) key) =>
        values.TryGetValue(key, out var bytes) && bytes <= long.MaxValue ? (long)bytes : null;

    private Dictionary<(int Pid, string Adapter), double> Aggregate(string metric, bool perProcess,
        Dictionary<(string Adapter, string Engine), double>? hardwareEngines = null)
    {
        var result = new Dictionary<(int, string), double>();
        if (!_counters.TryGetValue(metric, out var counter)) return result;
        uint size = 0, count = 0;
        if (PdhGetFormattedCounterArray(counter, PdhDoubleNoCap, ref size, ref count, IntPtr.Zero) != PdhMoreData ||
            size is 0 or > 16 * 1024 * 1024) return result;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(counter, PdhDoubleNoCap, ref size, ref count, buffer) != 0) return result;
            var stride = Marshal.SizeOf<CounterItem>();
            if (count * (ulong)stride > size) return result;
            for (var index = 0; index < count; index++)
            {
                var item = Marshal.PtrToStructure<CounterItem>(IntPtr.Add(buffer, checked(index * stride)));
                if (item.Status is > 1 || !double.IsFinite(item.Value) || item.Value < 0) continue;
                var instance = Marshal.PtrToStringUni(item.Name);
                if (instance is null) continue;
                var adapter = AdapterPattern().Match(instance);
                if (!adapter.Success) continue;
                var pid = 0;
                if (perProcess && !int.TryParse(PidPattern().Match(instance).Groups[1].Value, out pid)) continue;
                var key = (pid, adapter.Value.ToLowerInvariant());
                var old = result.GetValueOrDefault(key);
                result[key] = hardwareEngines is null ? old + item.Value : Math.Max(old, item.Value);
                if (hardwareEngines is not null)
                {
                    var engine = EnginePattern().Match(instance, adapter.Index + adapter.Length);
                    if (engine.Success)
                    {
                        var address = (key.Item2, engine.Value.ToLowerInvariant());
                        // Sum callers of the same hardware engine. Report the busiest engine on each GPU.
                        hardwareEngines[address] = hardwareEngines.GetValueOrDefault(address) + item.Value;
                    }
                }
            }
            return result;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private double? ReadCpu()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return null;
        var net = kernel.Value + user.Value;
        var previous = _lastCpu;
        _lastCpu = (idle.Value, net);
        if (previous is not { } old || net <= old.Total || idle.Value < old.Idle) return null;
        return Math.Clamp(100.0 * (1 - (idle.Value - old.Idle) / (double)(net - old.Total)), 0, 100);
    }

    public void Dispose()
    {
        if (_query != 0) _ = PdhCloseQuery(_query);
        _query = 0;
    }

    [GeneratedRegex(@"luid_0x[\da-f]+_0x[\da-f]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AdapterPattern();
    [GeneratedRegex(@"^pid_(\d+)_", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PidPattern();
    [GeneratedRegex(@"\G_phys_\d+_eng_\d+(?=_|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EnginePattern();

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterItem { public nint Name; public uint Status; public double Value; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, ExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low, High;
        public readonly ulong Value => ((ulong)High << 32) | Low;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhOpenQueryW")]
    private static extern uint PdhOpenQuery(string? source, nint userData, out nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")]
    private static extern uint PdhAddEnglishCounter(nint query, string path, nint userData, out nint counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(nint query);
    [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    private static extern uint PdhGetFormattedCounterArray(nint counter, uint format, ref uint size, ref uint count, nint buffer);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(nint query);
}
