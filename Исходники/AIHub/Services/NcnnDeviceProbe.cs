using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AIHub.Services;

public sealed record NcnnDevice(int Index, string Name);

internal static partial class NcnnDeviceProbe
{
    [GeneratedRegex(@"^\[(?<id>\d+) (?<name>[^\]\r\n]+)\]\s+queueC=\d+\[(?<queues>\d+)\]", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex DeviceLine();

    internal static IReadOnlyList<NcnnDevice> Parse(string output)
    {
        var devices = new Dictionary<int, NcnnDevice>();
        foreach (Match match in DeviceLine().Matches(output))
        {
            if (!int.TryParse(match.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                || index is < 0 or > 15 || !int.TryParse(match.Groups["queues"].Value, out var queues) || queues <= 0)
                continue;
            var device = new NcnnDevice(index, match.Groups["name"].Value.Trim());
            if (devices.TryGetValue(index, out var prior) && prior != device)
                throw new InvalidDataException("Conflicting native device identities.");
            devices[index] = device;
        }
        return devices.Values.OrderBy(device => device.Index).ToArray();
    }

    internal static async Task<IReadOnlyList<NcnnDevice>> ReadAsync(string executable, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var absent = Path.Combine(Path.GetTempPath(), "lopata-ncnn-inventory-" + Guid.NewGuid().ToString("N"));
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetDirectoryName(executable)!, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        RuntimeDeviceProbe.ClearBackendOverrides(info);
        // The pinned executables enumerate Vulkan, then reject this ordinal before loading weights or files.
        foreach (var value in new[] { "-i", absent + "-input.png", "-o", absent + "-output.png", "-g", "2147483647" })
            info.ArgumentList.Add(value);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var process = OwnedProcessRegistry.Shared.Start(info, "ImageUtility.NcnnInventory");
        var stdout = ReadBoundedAsync(process.StandardOutput, timeout.Token);
        var stderr = ReadBoundedAsync(process.StandardError, timeout.Token);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), stdout, stderr);
            var text = await stdout + "\n" + await stderr;
            if (process.ExitCode != -1 || !text.Contains("invalid gpu device", StringComparison.Ordinal))
                throw new IOException("Native device inventory did not reach the pinned validation point: " + text);
            return Parse(text);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("Native device inventory timed out."); }
        finally
        {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
            try { await Task.WhenAll(stdout, stderr); } catch (OperationCanceledException) { }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
        {
            if (result.Length + count > 32768) throw new InvalidDataException("Native inventory output exceeds its bound.");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }
}
