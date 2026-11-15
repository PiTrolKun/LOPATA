using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace AIHub.Services;

public static class RuntimeResourceDiagnostics
{
    private const uint Th32CsSnapProcess = 0x00000002;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    public static string DescribeLaunch(
        string component,
        Process process,
        string placement,
        string? modelPath = null)
    {
        var model = DescribeModel(modelPath);
        return $"Runtime launch: component={component}; pid={process.Id}; " +
            $"placement={placement}; {model}";
    }

    public static string DescribeSystemMemory(string phase)
    {
        var memory = ReadPhysicalMemory();
        var used = Math.Max(0, memory.TotalBytes - memory.AvailableBytes);
        return $"System memory: phase={phase}; availableBytes={memory.AvailableBytes}; " +
            $"usedBytes={used}; totalBytes={memory.TotalBytes}.";
    }

    public static string DescribeSnapshot(
        string component,
        Process process,
        string phase)
    {
        var snapshot = Capture(process);
        return $"Runtime resources: component={component}; phase={phase}; " +
            $"rootPid={snapshot.RootProcessId}; processCount={snapshot.ProcessCount}; " +
            $"workingSetBytes={snapshot.WorkingSetBytes}; " +
            $"privateBytes={snapshot.PrivateBytes}; " +
            $"peakWorkingSetBytes={snapshot.PeakWorkingSetBytes}; " +
            $"virtualBytes={snapshot.VirtualBytes}; " +
            $"systemAvailableBytes={snapshot.SystemAvailableBytes}; " +
            $"systemTotalBytes={snapshot.SystemTotalBytes}.";
    }

    public static RuntimeResourceSnapshot Capture(Process rootProcess)
    {
        ArgumentNullException.ThrowIfNull(rootProcess);
        var processIds = EnumerateProcessTree(rootProcess.Id);
        long workingSet = 0;
        long privateBytes = 0;
        long peakWorkingSet = 0;
        long virtualBytes = 0;
        var processCount = 0;

        foreach (var processId in processIds)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                process.Refresh();
                if (process.HasExited)
                {
                    continue;
                }

                workingSet = SaturatingAdd(workingSet, process.WorkingSet64);
                privateBytes = SaturatingAdd(privateBytes, process.PrivateMemorySize64);
                peakWorkingSet = SaturatingAdd(peakWorkingSet, process.PeakWorkingSet64);
                virtualBytes = SaturatingAdd(virtualBytes, process.VirtualMemorySize64);
                processCount++;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // A short-lived child may disappear while the snapshot is collected.
            }
        }

        var memory = ReadPhysicalMemory();
        return new RuntimeResourceSnapshot(
            rootProcess.Id,
            processCount,
            workingSet,
            privateBytes,
            peakWorkingSet,
            virtualBytes,
            memory.AvailableBytes,
            memory.TotalBytes);
    }

    private static string DescribeModel(string? modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
        {
            return "model=none; modelFileBytes=0.";
        }

        try
        {
            var info = new FileInfo(modelPath);
            return $"model={info.Name}; modelFileBytes={(info.Exists ? info.Length : 0)}.";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return "model=unavailable; modelFileBytes=0.";
        }
    }

    private static IReadOnlyCollection<int> EnumerateProcessTree(int rootProcessId)
    {
        var parentByProcess = ReadParentProcessIds();
        var result = new HashSet<int> { rootProcessId };
        var queue = new Queue<int>();
        queue.Enqueue(rootProcessId);

        while (queue.Count > 0)
        {
            var parentId = queue.Dequeue();
            foreach (var item in parentByProcess)
            {
                if (item.Value != parentId || !result.Add(item.Key))
                {
                    continue;
                }

                queue.Enqueue(item.Key);
            }
        }

        return result;
    }

    private static Dictionary<int, int> ReadParentProcessIds()
    {
        var result = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(Th32CsSnapProcess, 0);
        if (snapshot == InvalidHandleValue)
        {
            return result;
        }

        try
        {
            var entry = new ProcessEntry32
            {
                Size = (uint)Marshal.SizeOf<ProcessEntry32>()
            };
            if (!Process32First(snapshot, ref entry))
            {
                return result;
            }

            do
            {
                result[(int)entry.ProcessId] = (int)entry.ParentProcessId;
                entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
            }
            while (Process32Next(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return result;
    }

    internal static (long AvailableBytes, long TotalBytes) ReadPhysicalMemory()
    {
        var status = new MemoryStatusEx();
        if (!GlobalMemoryStatusEx(status))
        {
            return (0, 0);
        }

        return (
            status.AvailablePhysical > long.MaxValue ? long.MaxValue : (long)status.AvailablePhysical,
            status.TotalPhysical > long.MaxValue ? long.MaxValue : (long)status.TotalPhysical);
    }

    private static long SaturatingAdd(long left, long right) =>
        right > 0 && long.MaxValue - left < right ? long.MaxValue : left + Math.Max(0, right);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExecutableFile;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private sealed class MemoryStatusEx
    {
        public uint Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }
}

public sealed record RuntimeResourceSnapshot(
    int RootProcessId,
    int ProcessCount,
    long WorkingSetBytes,
    long PrivateBytes,
    long PeakWorkingSetBytes,
    long VirtualBytes,
    long SystemAvailableBytes,
    long SystemTotalBytes);
