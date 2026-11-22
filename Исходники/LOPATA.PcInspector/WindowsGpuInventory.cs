using System.Runtime.InteropServices;

namespace Lopata.PcInspector;

internal sealed record GpuIdentity(string Name, string Adapter, long CapacityBytes);

internal static class WindowsGpuInventory
{
    internal static GpuIdentity[] Read()
    {
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        var result = new List<GpuIdentity>();
        if (CreateDXGIFactory1(ref iid, out var factory) < 0) return [];
        try
        {
            var enumerate = Method<EnumAdapter>(factory, 12);
            for (uint index = 0; index < 64; index++)
            {
                if (enumerate(factory, index, out var adapter) < 0) break;
                try
                {
                    if (Method<GetDescription>(adapter, 8)(adapter, out var desc) < 0) continue;
                    var bytes = desc.DedicatedVideoMemory.ToUInt64();
                    if (bytes <= long.MaxValue)
                        result.Add(new GpuIdentity(desc.Description.Trim(),
                            $"luid_0x{unchecked((uint)desc.LuidHigh):x8}_0x{desc.LuidLow:x8}", (long)bytes));
                }
                finally { Marshal.Release(adapter); }
            }
        }
        finally { Marshal.Release(factory); }
        return result.ToArray();
    }

    private static T Method<T>(nint obj, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size));

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDescription
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint Vendor, Device, Subsystem, Revision;
        public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapter(nint factory, uint index, out nint adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDescription(nint adapter, out AdapterDescription description);
    [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(ref Guid iid, out nint factory);
}
