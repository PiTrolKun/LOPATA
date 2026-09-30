using System.Diagnostics;
using System.Globalization;
using System.IO;
using CsvHelper;
using CsvHelper.Configuration;

namespace AIHub.Services;

public sealed record LiteraryGpuSnapshot(string BusId, string Name, long TotalBytes, long UsedBytes, long FreeBytes);

// Inventory only: never loads a model or changes the runtime's allocation policy.
public sealed class LiteraryGpuTelemetry
{
    private Task<string?>? _cudaBusId;
    public async Task<LiteraryGpuSnapshot?> ReadAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var info = new ProcessStartInfo("nvidia-smi.exe") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add("--query-gpu=pci.bus_id,name,memory.total,memory.used,memory.free");
            info.ArgumentList.Add("--format=csv,noheader,nounits");
            using var process = OwnedProcessRegistry.Shared.Start(info, "Literary readiness inventory");
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var error = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                var rows = Parse(await output.ConfigureAwait(false));
                await error.ConfigureAwait(false);
                if (process.ExitCode != 0) return null;
                if (rows.Count == 1 && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CUDA_VISIBLE_DEVICES"))) return rows[0];
                _cudaBusId ??= Task.Run(LiteraryCudaIdentity.ReadBusId);
                return Select(rows, await _cudaBusId.WaitAsync(token).ConfigureAwait(false));
            }
            finally { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or CsvHelperException or FormatException or OverflowException) { return null; }
    }

    public static IReadOnlyList<LiteraryGpuSnapshot> Parse(string text)
    {
        using var reader = new StringReader(text);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture) { HasHeaderRecord = false, TrimOptions = TrimOptions.Trim });
        var result = new List<LiteraryGpuSnapshot>();
        while (csv.Read())
        {
            if (csv.Parser.Count != 5) throw new FormatException("Incomplete GPU inventory.");
            var bus = NormalizeBusId(csv.GetField(0));
            var name = csv.GetField(1)?.Trim();
            long Bytes(int index) => checked(long.Parse(csv.GetField(index)!, CultureInfo.InvariantCulture) * LiteraryAutomaticBudget.MiB);
            var total = Bytes(2); var used = Bytes(3); var free = Bytes(4);
            if (bus is null || string.IsNullOrWhiteSpace(name) || total <= 0 || used < 0 || free < 0 || used > total || free > total)
                throw new FormatException("Invalid GPU inventory.");
            result.Add(new(bus, name, total, used, free));
        }
        return result;
    }
    public static LiteraryGpuSnapshot? Select(IReadOnlyList<LiteraryGpuSnapshot> rows, string? cudaBusId)
    {
        var bus = NormalizeBusId(cudaBusId);
        if (bus is null) return null;
        var matches = rows.Where(row => row.BusId == bus).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
    public static string? NormalizeBusId(string? value)
    {
        var parts = value?.Trim().Split([':', '.']);
        if (parts?.Length != 4) return null;
        var numbers = new uint[4];
        for (var i = 0; i < parts.Length; i++)
            if (!uint.TryParse(parts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out numbers[i])) return null;
        return string.Join(":", numbers.Select(x => x.ToString("X", CultureInfo.InvariantCulture)));
    }
}
