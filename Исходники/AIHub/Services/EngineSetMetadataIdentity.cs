using System.IO;
using System.Text.Json;

namespace AIHub.Services;

/// <summary>Formats recorded execution identity only; never consults today's selected engine set.</summary>
internal static class EngineSetMetadataIdentity
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Dates = new(ReadDates);

    internal static string? Date(string? recordedId)
    {
        if (recordedId is null) return null;
        EngineSetSelection.ValidateId(recordedId);
        return Dates.Value.GetValueOrDefault(recordedId);
    }

    internal static string Producer(string appVersion, string? recordedId) =>
        "LOPATA " + appVersion + "; engine set " + (recordedId ?? "unknown")
        + (Date(recordedId) is { } date ? " (" + date + ")" : "");

    internal static void AddTags(IDictionary<string, string> tags, string? recordedId)
    {
        tags["LOPATA_ENGINE_SET"] = recordedId ?? "unknown";
        if (Date(recordedId) is { } date) tags["LOPATA_ENGINE_SET_DATE"] = date;
    }

    private static IReadOnlyDictionary<string, string> ReadDates()
    {
        using var stream = typeof(EngineSetMetadataIdentity).Assembly.GetManifestResourceStream("AIHub.EngineSets.Catalog")
            ?? throw new InvalidDataException("Missing engine set dates.");
        using var json = JsonDocument.Parse(stream);
        return json.RootElement.GetProperty("sets").EnumerateArray().ToDictionary(
            set => set.GetProperty("id").GetString()!, set => set.GetProperty("date").GetString()!, StringComparer.Ordinal);
    }
}
