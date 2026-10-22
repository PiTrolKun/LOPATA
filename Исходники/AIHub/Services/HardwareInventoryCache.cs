using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Reuse static GPU metadata in quick mode; runtime execution still checks actual devices/resources.</summary>
internal static class HardwareInventoryCache
{
    private sealed record Snapshot(string Identity, int[] ComputeMajors);

    internal static IReadOnlyList<int> ComputeMajors(IEnumerable<GpuPassport> gpus, bool fullAudit,
        Func<IReadOnlyList<int>> probe, string? cachePath = null)
    {
        var identity = Environment.MachineName + "|" + string.Join('|', gpus.Select(g => g.Name).Order(StringComparer.Ordinal));
        var path = cachePath ?? Path.Combine(AppDataPaths.BaseDirectory, "hardware-startup-cache.json");
        if (!fullAudit)
        {
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length <= 65536)
                {
                    var saved = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(path));
                    if (saved?.Identity == identity && saved.ComputeMajors is {Length: <= 100}
                        && saved.ComputeMajors.All(m => m is >= 1 and <= 99)) return saved.ComputeMajors;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        }
        var result = probe().ToArray();
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Snapshot(identity, result)));
            File.Move(temporary, path, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { OwnedProcessRegistry.Log("hardware_inventory_cache_unavailable", "Startup", detail: error.GetType().Name); }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return result;
    }
}
