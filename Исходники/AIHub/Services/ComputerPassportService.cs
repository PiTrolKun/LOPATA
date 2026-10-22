using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AIHub.Models;
using Microsoft.Win32;

namespace AIHub.Services;

public sealed class ComputerPassportService(string? passportPath = null)
{
    private readonly string _passportPath = passportPath ?? AppDataPaths.ComputerPassportPath;
    private const double BytesInGb = 1024d * 1024d * 1024d;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public ComputerPassport EnsurePassport()
    {
        AppDataPaths.EnsureBaseDirectory();

        if (!File.Exists(_passportPath))
        {
            return RegeneratePassport();
        }

        try
        {
            var json = File.ReadAllText(_passportPath);
            var passport = JsonSerializer.Deserialize<ComputerPassport>(json, JsonOptions)
                ?? RegeneratePassport();
            if (passport.LogicalProcessorCount <= 0)
            {
                passport.LogicalProcessorCount = GetLogicalProcessorCount();
                SavePassport(passport);
            }

            return passport;
        }
        catch
        {
            return RegeneratePassport();
        }
    }

    public ComputerPassport RegeneratePassport()
    {
        AppDataPaths.EnsureBaseDirectory();

        var passport = new ComputerPassport
        {
            CreatedAt = DateTimeOffset.Now,
            MachineName = Environment.MachineName,
            WindowsVersion = Environment.OSVersion.VersionString,
            OperatingSystemArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            UserName = Environment.UserName,
            CpuName = GetCpuName(),
            LogicalProcessorCount = GetLogicalProcessorCount(),
            RamTotalGb = GetTotalMemoryGb(),
            Gpus = GetGpus(),
            Drives = GetDrives()
        };

        SavePassport(passport);

        return passport;
    }

    private static int GetLogicalProcessorCount() => Math.Max(1, Environment.ProcessorCount);

    public ComputerPassport ForStartup(bool fullAudit) => fullAudit ? RegeneratePassport() : EnsurePassport();

    private void SavePassport(ComputerPassport passport)
    {
        var json = JsonSerializer.Serialize(passport, JsonOptions);
        File.WriteAllText(_passportPath, json);
    }

    private static string GetCpuName()
    {
        const string cpuKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";

        return Registry.LocalMachine
            .OpenSubKey(cpuKey)
            ?.GetValue("ProcessorNameString")
            ?.ToString()
            ?.Trim() ?? "unknown";
    }

    private static double GetTotalMemoryGb()
    {
        var memoryStatus = new MemoryStatusEx();
        if (!GlobalMemoryStatusEx(memoryStatus))
        {
            return 0;
        }

        return RoundGb(memoryStatus.TotalPhysicalMemory);
    }

    private static List<DrivePassport> GetDrives()
    {
        var drives = new List<DrivePassport>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady)
                {
                    continue;
                }

                drives.Add(new DrivePassport
                {
                    Name = drive.Name,
                    DriveType = drive.DriveType.ToString(),
                    TotalGb = RoundGb((ulong)drive.TotalSize),
                    FreeGb = RoundGb((ulong)drive.AvailableFreeSpace)
                });
            }
            catch
            {
                // Skip a drive if Windows denies details or the drive disappears during the scan.
            }
        }

        return drives;
    }

    private static List<GpuPassport> GetGpus()
    {
        const string videoKey = @"SYSTEM\CurrentControlSet\Control\Video";

        var gpus = new Dictionary<string, GpuPassport>(StringComparer.OrdinalIgnoreCase);
        using var rootKey = Registry.LocalMachine.OpenSubKey(videoKey);

        if (rootKey is null)
        {
            return [];
        }

        foreach (var adapterKeyName in rootKey.GetSubKeyNames())
        {
            using var adapterKey = rootKey.OpenSubKey(adapterKeyName);
            if (adapterKey is null)
            {
                continue;
            }

            foreach (var childKeyName in adapterKey.GetSubKeyNames())
            {
                using var childKey = adapterKey.OpenSubKey(childKeyName);
                if (childKey is null)
                {
                    continue;
                }

                var name = ReadRegistryString(
                    childKey.GetValue("HardwareInformation.AdapterString"))
                    ?? ReadRegistryString(childKey.GetValue("DriverDesc"));

                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var vramGb = GetGpuMemoryGb(childKey);
                if (gpus.TryGetValue(name, out var existingGpu))
                {
                    existingGpu.VramGb = Math.Max(existingGpu.VramGb, vramGb);
                    continue;
                }

                gpus[name] = new GpuPassport
                {
                    Name = name,
                    VramGb = vramGb
                };
            }
        }

        return gpus.Values.ToList();
    }

    private static double GetGpuMemoryGb(RegistryKey registryKey)
    {
        var memoryValue =
            registryKey.GetValue("HardwareInformation.qwMemorySize")
            ?? registryKey.GetValue("HardwareInformation.MemorySize")
            ?? registryKey.GetValue("MemorySize");

        var bytes = ReadRegistryUInt64(memoryValue);
        return bytes == 0 ? 0 : RoundGb(bytes);
    }

    private static string? ReadRegistryString(object? value)
    {
        return value switch
        {
            string text when !string.IsNullOrWhiteSpace(text) => text.Trim(),
            byte[] bytes when bytes.Length > 0 => Encoding.Unicode.GetString(bytes).TrimEnd('\0').Trim(),
            _ => null
        };
    }

    private static ulong ReadRegistryUInt64(object? value)
    {
        return value switch
        {
            int number when number > 0 => (ulong)number,
            uint number => number,
            long number when number > 0 => (ulong)number,
            ulong number => number,
            byte[] bytes when bytes.Length >= 8 => BitConverter.ToUInt64(bytes, 0),
            byte[] bytes when bytes.Length >= 4 => BitConverter.ToUInt32(bytes, 0),
            _ => 0
        };
    }

    private static double RoundGb(ulong bytes)
    {
        return Math.Round(bytes / BytesInGb, 2);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private sealed class MemoryStatusEx
    {
        private readonly uint _length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        private readonly uint _memoryLoad;

        public ulong TotalPhysicalMemory;
        private readonly ulong _availablePhysicalMemory;
        private readonly ulong _totalPageFile;
        private readonly ulong _availablePageFile;
        private readonly ulong _totalVirtual;
        private readonly ulong _availableVirtual;
        private readonly ulong _availableExtendedVirtual;
    }
}
