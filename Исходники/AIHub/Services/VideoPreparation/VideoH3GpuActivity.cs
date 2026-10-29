using System.Runtime.InteropServices;
using System.Text.Json;

namespace AIHub.Services.VideoPreparation;

/// <summary>Windows GPU-engine counters for the owned worker; no polling subprocess or vendor restriction.</summary>
internal sealed class VideoH3GpuActivity : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _work;
    internal VideoH3GpuActivity(int pid, Action<string> log) => _work = Task.Run(async () =>
    {
        nint query = 0;
        try
        {
            if (PdhOpenQuery(null, 0, out query) != 0
                || PdhAddEnglishCounter(query, @"\GPU Engine(*)\Utilization Percentage", 0, out var counter) != 0) return;
            PdhCollectQueryData(query);
            while (!_stop.IsCancellationRequested)
            {
                await Task.Delay(3000, _stop.Token);
                if (PdhCollectQueryData(query) != 0) continue;
                uint bytes = 0, count = 0;
                if (PdhGetFormattedCounterArray(counter, 0x200, ref bytes, ref count, 0) != 0x800007D2 || bytes > 8_000_000) continue;
                var buffer = Marshal.AllocHGlobal((int)bytes);
                try
                {
                    if (PdhGetFormattedCounterArray(counter, 0x200, ref bytes, ref count, buffer) != 0) continue;
                    double? value = null;
                    for (var i = 0; i < count; i++)
                    {
                        var sample = Marshal.PtrToStructure<CounterItem>(buffer + i * Marshal.SizeOf<CounterItem>());
                        var name = Marshal.PtrToStringUni(sample.Name);
                        if (sample.Status > 1 || name?.StartsWith("pid_" + pid + "_", StringComparison.OrdinalIgnoreCase) != true) continue;
                        if (double.IsFinite(sample.Value)) value = Math.Max(value ?? 0, sample.Value);
                    }
                    if (value is not null) log("[H3 event] " + JsonSerializer.Serialize(new { kind = "gpu", utilization = Math.Clamp(value.Value, 0, 100) }));
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { }
        finally { if (query != 0) PdhCloseQuery(query); }
    });
    public void Dispose() { _stop.Cancel(); _work.GetAwaiter().GetResult(); _stop.Dispose(); }
    [StructLayout(LayoutKind.Sequential)]
    private struct CounterItem { public nint Name; public uint Status; private uint _padding; public double Value; }
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhOpenQueryW")]
    private static extern uint PdhOpenQuery(string? source, nuint data, out nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")]
    private static extern uint PdhAddEnglishCounter(nint query, string path, nuint data, out nint counter);
    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(nint query);
    [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    private static extern uint PdhGetFormattedCounterArray(nint counter, uint format, ref uint size, ref uint count, nint buffer);
    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(nint query);
}
