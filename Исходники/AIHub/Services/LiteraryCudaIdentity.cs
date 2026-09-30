using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AIHub.Services;

internal static class LiteraryCudaIdentity
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Init(uint flags);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int DeviceGet(out int device, int ordinal);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int BusGet([Out, MarshalAs(UnmanagedType.LPStr)] StringBuilder bus, int length, int device);
    public static string? ReadBusId()
    {
        if (!OperatingSystem.IsWindows()) return null;
        nint library = 0;
        try
        {
            // The same CUDA0 ordinal as the literary backend. No CUDA context or model is created.
            library = NativeLibrary.Load(Path.Combine(Environment.SystemDirectory, "nvcuda.dll"));
            var init = Marshal.GetDelegateForFunctionPointer<Init>(NativeLibrary.GetExport(library, "cuInit"));
            var get = Marshal.GetDelegateForFunctionPointer<DeviceGet>(NativeLibrary.GetExport(library, "cuDeviceGet"));
            var bus = Marshal.GetDelegateForFunctionPointer<BusGet>(NativeLibrary.GetExport(library, "cuDeviceGetPCIBusId"));
            if (init(0) != 0 || get(out var device, 0) != 0) return null;
            var text = new StringBuilder(32);
            return bus(text, text.Capacity, device) == 0 ? text.ToString() : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { return null; }
        finally { if (library != 0) NativeLibrary.Free(library); }
    }
}
