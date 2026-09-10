using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AIHub.Services;

internal sealed class WindowsProcessJob : IDisposable
{
    private readonly SafeJobHandle _handle;
    public WindowsProcessJob()
    {
        _handle = CreateJobObject(IntPtr.Zero, null);
        if (_handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } }; // KILL_ON_JOB_CLOSE
        if (!SetInformationJobObject(_handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
        { var error = Marshal.GetLastWin32Error(); _handle.Dispose(); throw new Win32Exception(error); }
    }
    public void Assign(Process process) => Assign(process.Handle);
    public void Assign(IntPtr process)
    {
        if (!AssignProcessToJobObject(_handle, process)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public void Dispose() => _handle.Dispose();

    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        public long PerProcessTime, PerJobTime;
        public uint Flags;
        public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong A, B, C, D, E, F; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeJobHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeJobHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(SafeJobHandle job, int infoClass, ref ExtendedLimits limits, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(SafeJobHandle job, IntPtr process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
