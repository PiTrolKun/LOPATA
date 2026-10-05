using System.IO;
using System.Runtime.InteropServices;

namespace AIHub.Services;

/// <summary>Checks the CUDA0 driver and the SM89 profile shipped with YuE2, without loading a model.</summary>
internal static class MusicCudaSupport
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Init(uint flags);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int DriverVersion(out int version);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int DeviceGet(out int device, int ordinal);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Capability(out int major, out int minor, int device);
    private static readonly Lazy<bool> Supported = new(Read);
    public static bool IsAvailable => Supported.Value;

    private static bool Read()
    {
        if (!OperatingSystem.IsWindows()) return false;
        nint library = 0;
        try
        {
            library = NativeLibrary.Load(Path.Combine(Environment.SystemDirectory, "nvcuda.dll"));
            var init = Marshal.GetDelegateForFunctionPointer<Init>(NativeLibrary.GetExport(library, "cuInit"));
            var version = Marshal.GetDelegateForFunctionPointer<DriverVersion>(NativeLibrary.GetExport(library, "cuDriverGetVersion"));
            var device = Marshal.GetDelegateForFunctionPointer<DeviceGet>(NativeLibrary.GetExport(library, "cuDeviceGet"));
            var capability = Marshal.GetDelegateForFunctionPointer<Capability>(NativeLibrary.GetExport(library, "cuDeviceComputeCapability"));
            return version(out var driver) == 0 && driver >= 12080 && init(0) == 0
                && device(out var id, 0) == 0 && capability(out var major, out var minor, id) == 0
                && major == 8 && minor == 9;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { return false; }
        finally { if (library != 0) NativeLibrary.Free(library); }
    }
}
