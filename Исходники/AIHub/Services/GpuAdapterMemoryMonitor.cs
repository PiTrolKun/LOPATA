using System.IO;
using System.Runtime.InteropServices;

namespace AIHub.Services;

/// <summary>Adapter-wide Windows VRAM counters, matched by DXGI LUID. Vulkan heap usage is process-local.</summary>
internal sealed class GpuAdapterMemoryMonitor : IDisposable
{
    private readonly RuntimeDevice _device;
    private readonly string _instancePrefix;
    private readonly long _total;
    private IntPtr _query, _counter;
    internal GpuAdapterMemoryMonitor(RuntimeDevice device)
    {
        _device = device;
        var matches = Adapters().Where(a => a.Name.Equals(device.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1 || matches[0].Total <= 0) throw new IOException("Selected GPU has no unambiguous dedicated-memory adapter.");
        _total = matches[0].Total; _instancePrefix = matches[0].Luid + "_";
        try
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0
                || PdhAddEnglishCounter(_query, @"\GPU Adapter Memory(*)\Dedicated Usage", IntPtr.Zero, out _counter) != 0
                || PdhCollectQueryData(_query) != 0) throw new IOException("Adapter VRAM counters are unavailable.");
        }
        catch { Dispose(); throw; }
    }
    internal RuntimeDevice Sample()
    {
        if (_query == IntPtr.Zero || PdhCollectQueryData(_query) != 0) throw new IOException("Adapter VRAM sampling failed.");
        uint size = 0, count = 0;
        var status = PdhGetFormattedCounterArray(_counter, 0x200, ref size, ref count, IntPtr.Zero);
        if (status != 0x800007D2 || size is 0 or > 4 * 1024 * 1024) throw new IOException("Adapter VRAM data is unavailable.");
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(_counter, 0x200, ref size, ref count, buffer) != 0) throw new IOException("Adapter VRAM counters changed during sampling.");
            if ((ulong)count * (uint)Marshal.SizeOf<CounterItem>() > size) throw new IOException("Invalid adapter counter array.");
            var found = false; double usage = 0;
            for (var index = 0; index < count; index++)
            {
                var item = Marshal.PtrToStructure<CounterItem>(IntPtr.Add(buffer, checked((int)index * Marshal.SizeOf<CounterItem>())));
                var instance = Marshal.PtrToStringUni(item.Name) ?? "";
                if (!instance.StartsWith(_instancePrefix, StringComparison.OrdinalIgnoreCase) || item.Status > 1) continue;
                if (!double.IsFinite(item.Value) || item.Value < 0) throw new IOException("Invalid adapter memory usage.");
                usage += item.Value; found = true;
            }
            if (!found || usage > long.MaxValue) throw new IOException("Selected adapter has no valid VRAM counters.");
            return _device with { TotalBytes = _total, FreeBytes = Math.Max(0, _total - (long)usage) };
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    internal static IReadOnlyList<(string Name, long Total, string Luid)> Adapters()
    {
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        Marshal.ThrowExceptionForHR(CreateDXGIFactory1(ref iid, out var factory));
        var result = new List<(string, long, string)>();
        try
        {
            var enumerate = Method<EnumAdapter>(factory, 12);
            for (uint index = 0; index < 64; index++)
            {
                var status = enumerate(factory, index, out var adapter);
                if (status == unchecked((int)0x887A0002)) break;
                Marshal.ThrowExceptionForHR(status);
                try
                {
                    Marshal.ThrowExceptionForHR(Method<GetDescription>(adapter, 8)(adapter, out var desc));
                    var bytes = desc.DedicatedVideoMemory.ToUInt64();
                    if (bytes <= long.MaxValue) result.Add((desc.Description.Trim(), (long)bytes,
                        $"luid_0x{unchecked((uint)desc.LuidHigh):x8}_0x{desc.LuidLow:x8}"));
                }
                finally { Marshal.Release(adapter); }
            }
        }
        finally { Marshal.Release(factory); }
        return result;
    }
    private static T Method<T>(IntPtr obj, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size));
    public void Dispose() { if (_query != IntPtr.Zero) { PdhCloseQuery(_query); _query = IntPtr.Zero; } }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDescription
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint Vendor, Device, Subsystem, Revision;
        public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow; public int LuidHigh;
    }
    [StructLayout(LayoutKind.Sequential)] private struct CounterItem { public IntPtr Name; public uint Status; public double Value; }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumAdapter(IntPtr factory, uint index, out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetDescription(IntPtr adapter, out AdapterDescription description);
    [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhOpenQueryW")] private static extern uint PdhOpenQuery(string? source, IntPtr data, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")] private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr data, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")] private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint size, ref uint count, IntPtr buffer);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
}
