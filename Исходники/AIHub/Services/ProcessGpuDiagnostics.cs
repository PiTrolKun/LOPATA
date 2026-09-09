using System.Runtime.InteropServices;

namespace AIHub.Services;

/// <summary>Windows per-process GPU counters; unavailable is distinct from zero.</summary>
internal sealed class ProcessGpuDiagnostics : IDisposable
{
    private IntPtr _query;
    private readonly Dictionary<string, IntPtr> _counters = [];
    private uint _status;
    public ProcessGpuDiagnostics()
    {
        _status = PdhOpenQuery(null, IntPtr.Zero, out _query);
        if (_status != 0) return;
        foreach (var (name, path) in new[] {
            ("dedicatedBytes", @"\GPU Process Memory(*)\Dedicated Usage"),
            ("sharedBytes", @"\GPU Process Memory(*)\Shared Usage"),
            ("engineUtilizationPercent", @"\GPU Engine(*)\Utilization Percentage") })
        {
            var status = PdhAddEnglishCounter(_query, path, IntPtr.Zero, out var counter);
            if (status == 0) _counters[name] = counter;
        }
        _status = PdhCollectQueryData(_query);
    }
    public object Sample(int pid)
    {
        if (_query == IntPtr.Zero) return new { available = false, status = _status };
        var status = PdhCollectQueryData(_query);
        var result = new Dictionary<string, object> { ["collectionStatus"] = status };
        foreach (var name in new[] { "dedicatedBytes", "sharedBytes", "engineUtilizationPercent" })
        {
            if (!_counters.TryGetValue(name, out var counter)) { result[name] = new { available = false }; continue; }
            uint size = 0, count = 0;
            var code = PdhGetFormattedCounterArray(counter, 0x200, ref size, ref count, IntPtr.Zero);
            if (code != 0x800007D2 || size == 0) { result[name] = new { available = false, status = code }; continue; }
            var buffer = Marshal.AllocHGlobal(checked((int)size));
            try
            {
                code = PdhGetFormattedCounterArray(counter, 0x200, ref size, ref count, buffer);
                var values = new Dictionary<string, double>();
                if (code == 0)
                    for (var i = 0; i < count; i++)
                    {
                        var item = Marshal.PtrToStructure<CounterItem>(IntPtr.Add(buffer, i * Marshal.SizeOf<CounterItem>()));
                        var instance = Marshal.PtrToStringUni(item.Name) ?? "";
                        if (instance.StartsWith($"pid_{pid}_", StringComparison.Ordinal) && item.Status <= 1)
                            values[instance] = item.Value;
                    }
                // Keep engine instances separate: adding engines does not mean total GPU utilization.
                result[name] = new { available = code == 0 && values.Count > 0, status = code, instances = values };
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return result;
    }
    public void Dispose() { if (_query != IntPtr.Zero) { PdhCloseQuery(_query); _query = IntPtr.Zero; } }
    [StructLayout(LayoutKind.Sequential)]
    private struct CounterItem { public IntPtr Name; public uint Status; public double Value; }
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhOpenQueryW")]
    private static extern uint PdhOpenQuery(string? source, IntPtr data, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr data, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint size, ref uint count, IntPtr buffer);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
}
