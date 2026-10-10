using System.IO;
using System.Text.Json;
using Lopata.Updates;

namespace AIHub.Services;

internal static class ApplicationUpdateDiagnostics
{
    public static void RecordFailure(string? version, Exception error)
    {
        try
        {
            var folder = Path.Combine(AppDataPaths.BaseDirectory, "Diagnostics", "Updates");
            SafeUpdatePath.RejectLinks(folder);
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"download-{DateTime.UtcNow:yyyyMMddTHHmmss}-{Guid.NewGuid():N}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                utc = DateTimeOffset.UtcNow, version, stage = "prepare", error = error.ToString()
            }));
        }
        catch (Exception) { /* Diagnostic storage cannot change update or cancellation state. */ }
    }
}
