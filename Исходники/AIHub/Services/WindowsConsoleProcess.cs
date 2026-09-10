using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AIHub.Services;

/// <summary>Dedicated hidden console permits Qdrant's native Ctrl+C shutdown on Windows.</summary>
internal static class WindowsConsoleProcess
{
    internal static Process Start(ProcessStartInfo info, WindowsProcessJob job, string logPath)
    {
        if (!Path.IsPathFullyQualified(info.FileName)) throw new ArgumentException("Absolute executable path required.");
        if (!string.IsNullOrEmpty(info.Arguments)) throw new ArgumentException("Use ArgumentList.");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        using var log = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Inherit = true };
        var nul = CreateFile("NUL", 0x80000000, 3, ref attributes, 3, 0, IntPtr.Zero);
        if (nul == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var handle = log.SafeFileHandle.DangerousGetHandle();
        if (!SetHandleInformation(handle, 1, 1)) { CloseHandle(nul); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        IntPtr environment = IntPtr.Zero;
        IntPtr attributeList = IntPtr.Zero, inheritedHandles = IntPtr.Zero;
        var initialized = false;
        ProcessInformation child = default;
        try
        {
            var block = string.Join('\0', info.Environment.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => x.Key + "=" + x.Value)) + "\0\0";
            environment = Marshal.StringToHGlobalUni(block);
            var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Flags = 0x101, ShowWindow = 0,
                StdInput = nul, StdOutput = handle, StdError = handle };
            nuint bytes = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref bytes);
            attributeList = Marshal.AllocHGlobal(checked((int)bytes));
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref bytes)) throw new Win32Exception(Marshal.GetLastWin32Error());
            initialized = true;
            inheritedHandles = Marshal.AllocHGlobal(IntPtr.Size * 2);
            Marshal.WriteIntPtr(inheritedHandles, nul); Marshal.WriteIntPtr(inheritedHandles, IntPtr.Size, handle);
            if (!UpdateProcThreadAttribute(attributeList, 0, 0x20002, inheritedHandles, (nuint)(IntPtr.Size * 2), IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
            startup.Size = Marshal.SizeOf<StartupInfoEx>();
            var extended = new StartupInfoEx { Startup = startup, Attributes = attributeList };
            var command = new StringBuilder(string.Join(' ', new[] { info.FileName }.Concat(info.ArgumentList).Select(Quote)));
            // Suspend before assigning the job so Qdrant cannot spawn unowned children.
            if (!CreateProcess(info.FileName, command, IntPtr.Zero, IntPtr.Zero, true, 0x80414,
                environment, info.WorkingDirectory, ref extended, out child)) throw new Win32Exception(Marshal.GetLastWin32Error());
            job.Assign(child.Process);
            var process = Process.GetProcessById((int)child.Pid);
            try
            {
                _ = process.SafeHandle;
                if (ResumeThread(child.Thread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
                return process;
            }
            catch { process.Dispose(); throw; }
        }
        catch
        {
            if (child.Process != IntPtr.Zero) TerminateProcess(child.Process, 1);
            throw;
        }
        finally
        {
            if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
            if (initialized) DeleteProcThreadAttributeList(attributeList);
            if (attributeList != IntPtr.Zero) Marshal.FreeHGlobal(attributeList);
            if (inheritedHandles != IntPtr.Zero) Marshal.FreeHGlobal(inheritedHandles);
            if (child.Thread != IntPtr.Zero) CloseHandle(child.Thread);
            if (child.Process != IntPtr.Zero) CloseHandle(child.Process);
            SetHandleInformation(handle, 1, 0); CloseHandle(nul);
        }
    }

    // Run only in a short-lived helper: AttachConsole would mutate the main application's console.
    internal static int Signal(int pid, long startedTicks)
    {
        try
        {
            using var target = Process.GetProcessById(pid);
            _ = target.SafeHandle;
            if (target.StartTime.ToUniversalTime().Ticks != startedTicks || target.HasExited) return 2;
            FreeConsole();
            if (!AttachConsole((uint)pid)) return 3;
            try
            {
                SetConsoleCtrlHandler(IntPtr.Zero, true);
                return GenerateConsoleCtrlEvent(0, 0) ? 0 : 4;
            }
            finally { FreeConsole(); }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { return 5; }
    }

    private static string Quote(string value)
    {
        var result = new StringBuilder("\""); var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public int Size; public string? Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCount, YCount, Fill, Flags;
        public short ShowWindow, ReservedSize; public IntPtr ReservedPointer, StdInput, StdOutput, StdError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public uint Pid, Tid; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo Startup; public IntPtr Attributes; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcess(string executable, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateFile(string name, uint access, uint share, ref SecurityAttributes attributes, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll")] private static extern bool FreeConsole();
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(uint pid);
    [DllImport("kernel32.dll")] private static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
    [DllImport("kernel32.dll")] private static extern bool GenerateConsoleCtrlEvent(uint signal, uint group);
}
