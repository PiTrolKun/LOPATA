using System.IO;
using System.Runtime.InteropServices;

namespace AIHub.Services;

/// <summary>Driver metadata only: never creates a CUDA context or allocates model buffers.</summary>
internal static class NvidiaDriverCapabilities
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Init(uint flags);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Count(out int count);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Device(out int device, int ordinal);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Capability(out int major, out int minor, int device);

    internal static IReadOnlyList<int> ReadComputeMajors()
    {
        if (!OperatingSystem.IsWindows()) return [];
        nint library = 0;
        try
        {
            library = NativeLibrary.Load(Path.Combine(Environment.SystemDirectory, "nvcuda.dll"));
            var init = Marshal.GetDelegateForFunctionPointer<Init>(NativeLibrary.GetExport(library, "cuInit"));
            var count = Marshal.GetDelegateForFunctionPointer<Count>(NativeLibrary.GetExport(library, "cuDeviceGetCount"));
            var get = Marshal.GetDelegateForFunctionPointer<Device>(NativeLibrary.GetExport(library, "cuDeviceGet"));
            var capability = Marshal.GetDelegateForFunctionPointer<Capability>(NativeLibrary.GetExport(library, "cuDeviceComputeCapability"));
            if (init(0) != 0 || count(out var total) != 0 || total is < 1 or > 100) return [];
            var majors = new List<int>();
            for (var ordinal = 0; ordinal < total; ordinal++)
            {
                if (get(out var device, ordinal) != 0 || capability(out var major, out var minor, device) != 0
                    || major is < 1 or > 99 || minor is < 0 or > 99) return [];
                majors.Add(major);
            }
            return majors;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        { return []; }
        finally { if (library != 0) NativeLibrary.Free(library); }
    }
}
