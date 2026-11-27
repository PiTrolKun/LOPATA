using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace AIHub.Services.VisualConstructor;

internal static class ConstructorSandbox
{
    private const int SecurityCapabilitiesAttribute = 0x20009;
    private const int ChildProcessPolicyAttribute = 0x2000E;
    private const int ChildProcessRestricted = 1;
    private const uint Suspended = 0x00000004;
    private const uint UnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfo = 0x00080000;
    internal static async Task RunAsync(string executable, Action<string> started, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindowsVersionAtLeast(10))
            throw new PlatformNotSupportedException("VisualConstructor.IsolationUnavailable");
        var name = "Lopata.Preview." + Guid.NewGuid().ToString("N");
        Marshal.ThrowExceptionForHR(CreateAppContainerProfile(name, "LOPATA Preview", "Visual Constructor", IntPtr.Zero, 0, out var sid));
        try
        {
            var program = Path.GetDirectoryName(executable)!;
            var identity = new SecurityIdentifier(sid);
            var profile = ProfilePath(identity);
            var scratch = Directory.CreateDirectory(Path.Combine(profile, "Temp")).FullName;
            Grant(program, identity, FileSystemRights.ReadAndExecute);
            using var job = new WindowsProcessJob();
            using var process = CreateSuspended(executable, scratch, sid, job);
            try
            {
                started(scratch);
                await process.WaitForExitAsync(token).ConfigureAwait(false);
                if (process.ExitCode != 0) throw new IOException($"AppContainer: 0x{process.ExitCode:X8}");
            }
            catch
            {
                await job.TerminateAndWaitAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            FreeSid(sid);
            var result = DeleteAppContainerProfile(name);
            if (result < 0) Trace.TraceWarning("Visual Constructor preview profile could not be removed: {0} (0x{1:X8})", name, result);
        }
    }

    private static void Grant(string directory, SecurityIdentifier identity, FileSystemRights rights)
    {
        var folder = new DirectoryInfo(directory);
        var acl = folder.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(identity, rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        folder.SetAccessControl(acl);
    }

    private static string ProfilePath(SecurityIdentifier identity)
    {
        Marshal.ThrowExceptionForHR(GetAppContainerFolderPath(identity.Value, out var path));
        try { return Marshal.PtrToStringUni(path) ?? throw new IOException("VisualConstructor.IsolationUnavailable"); }
        finally { Marshal.FreeCoTaskMem(path); }
    }

    private static Process CreateSuspended(string executable, string scratch, IntPtr sid, WindowsProcessJob job)
    {
        nuint size = 0;
        InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
        var list = Marshal.AllocHGlobal((nint)size);
        var capabilities = Marshal.AllocHGlobal(Marshal.SizeOf<SecurityCapabilities>());
        var policy = Marshal.AllocHGlobal(sizeof(int));
        var systemRoot = Path.GetDirectoryName(Environment.SystemDirectory)!;
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        // Do not inherit host-specific paths, secrets, runtime injection variables or open handles.
        // GetTempPath2 derives the private AppContainer path from this standard Windows folder.
        var environment = Marshal.StringToHGlobalUni(
            $"LOCALAPPDATA={localData}\0LOPATA_CONSTRUCTOR_ISOLATED=1\0PATH={Environment.SystemDirectory}\0SystemRoot={systemRoot}\0TEMP={scratch}\0TMP={scratch}\0WINDIR={systemRoot}\0\0");
        var initialized = false;
        try
        {
            if (!InitializeProcThreadAttributeList(list, 2, 0, ref size))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            initialized = true;
            Marshal.StructureToPtr(new SecurityCapabilities { Sid = sid }, capabilities, false);
            Marshal.WriteInt32(policy, ChildProcessRestricted);
            SetAttribute(SecurityCapabilitiesAttribute, capabilities, Marshal.SizeOf<SecurityCapabilities>());
            SetAttribute(ChildProcessPolicyAttribute, policy, sizeof(int));

            var startup = new StartupInfoEx { Startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfoEx>() }, Attributes = list };
            if (!CreateProcess(executable, new StringBuilder($"\"{executable}\""), IntPtr.Zero, IntPtr.Zero, false,
                ExtendedStartupInfo | UnicodeEnvironment | Suspended, environment, scratch, ref startup, out var native))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                job.Assign(native.Process);
                var process = Process.GetProcessById(native.ProcessId);
                if (ResumeThread(native.Thread) != uint.MaxValue) return process;
                var error = Marshal.GetLastWin32Error();
                process.Dispose();
                throw new Win32Exception(error);
            }
            catch
            {
                TerminateProcess(native.Process, 1);
                throw;
            }
            finally { CloseHandle(native.Thread); CloseHandle(native.Process); }
        }
        finally
        {
            if (initialized) DeleteProcThreadAttributeList(list);
            Marshal.FreeHGlobal(list);
            Marshal.FreeHGlobal(capabilities);
            Marshal.FreeHGlobal(policy);
            Marshal.FreeHGlobal(environment);
        }

        void SetAttribute(int attribute, IntPtr value, int count)
        {
            if (!UpdateProcThreadAttribute(list, 0, attribute, value, (nuint)count, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityCapabilities
    {
        internal IntPtr Sid, Capabilities;
        internal uint Count, Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        internal int Size;
        internal IntPtr Reserved, Desktop, Title;
        internal uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        internal ushort ShowWindow, ReservedSize;
        internal IntPtr ReservedBytes, StandardInput, StandardOutput, StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx { internal StartupInfo Startup; internal IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { internal IntPtr Process, Thread; internal int ProcessId, ThreadId; }

    [DllImport("userenv.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CreateAppContainerProfile(string name, string display, string description, IntPtr caps, uint count, out IntPtr sid);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int DeleteAppContainerProfile(string name);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetAppContainerFolderPath(string sid, out IntPtr path);
    [DllImport("advapi32.dll")]
    private static extern IntPtr FreeSid(IntPtr sid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returnedSize);
    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string application, StringBuilder commandLine, IntPtr processSecurity, IntPtr threadSecurity,
        bool inheritHandles, uint flags, IntPtr environment, string workingDirectory, ref StartupInfoEx startup, out ProcessInfo process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);
}
