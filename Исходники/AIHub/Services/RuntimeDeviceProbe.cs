using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AIHub.Services;

public sealed record RuntimeDevice(string Id, string Name, long TotalBytes, long FreeBytes);

/// <summary>Uses the exact installed executable and its own device numbering, never a vendor name whitelist.</summary>
public static partial class RuntimeDeviceProbe
{
    [GeneratedRegex(@"^\s*(?<id>(?:CUDA|Vulkan|HIP|SYCL)\d+):\s*(?<name>.+)\s+\((?<total>\d+) MiB, (?<free>\d+) MiB free\)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex DeviceLine();

    public static IReadOnlyList<RuntimeDevice> ParseDevices(string output)
    {
        var devices = new List<RuntimeDevice>();
        foreach (Match line in DeviceLine().Matches(output))
        {
            if (!long.TryParse(line.Groups["total"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var total)
                || !long.TryParse(line.Groups["free"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var free)
                || total > long.MaxValue / 1048576 || free > total) continue;
            devices.Add(new(line.Groups["id"].Value, line.Groups["name"].Value.Trim(), total * 1048576, free * 1048576));
        }
        return devices;
    }

    public static RuntimeDevice? ChooseGpu(IEnumerable<RuntimeDevice> devices, long requiredBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requiredBytes);
        return devices.Where(device => device.FreeBytes > 0 && device.FreeBytes >= requiredBytes
            && device.TotalBytes >= device.FreeBytes)
            .OrderByDescending(device => device.FreeBytes).ThenBy(device => device.Id, StringComparer.Ordinal).FirstOrDefault();
    }

    public static async Task<string> RunAsync(string executable, string argument, CancellationToken token)
    {
        if (argument is not ("--version" or "--list-devices")) throw new ArgumentException("Only runtime inventory is allowed.", nameof(argument));
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetDirectoryName(executable)!, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        ClearBackendOverrides(info);
        info.ArgumentList.Add(argument);
        using var process = OwnedProcessRegistry.Shared.Start(info, "Runtime.HardwareProbe");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var output = ReadBoundedAsync(process.StandardOutput, timeout.Token);
        var errors = ReadBoundedAsync(process.StandardError, timeout.Token);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), output, errors);
            if (process.ExitCode != 0) throw new InvalidOperationException($"Runtime inventory exit {process.ExitCode}: {await errors}");
            return (await output) + "\n" + (await errors);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("Runtime hardware inventory timed out."); }
        finally
        {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); }
        }
    }

    public static void ClearBackendOverrides(ProcessStartInfo info)
    {
        foreach (var key in info.Environment.Keys.Where(key => key.StartsWith("LLAMA_ARG_", StringComparison.Ordinal)
            || key is "GGML_BACKEND_PATH" or "GGML_BACKEND" or "GGML_VK_VISIBLE_DEVICES" or "CUDA_VISIBLE_DEVICES").ToArray())
            info.Environment.Remove(key);
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder(); var buffer = new char[4096]; int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
        {
            if (text.Length + count > 131072) throw new InvalidDataException("Runtime inventory exceeds the output limit.");
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }
}
