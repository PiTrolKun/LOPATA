using System.Collections.ObjectModel;
using System.Text.Json;

namespace Lopata.Updates;

public static class UpdateReleaseKeys
{
    public static IReadOnlyDictionary<string, string> Trusted { get; } = Load();

    private static IReadOnlyDictionary<string, string> Load()
    {
        using var stream = typeof(UpdateReleaseKeys).Assembly.GetManifestResourceStream("Lopata.UpdateReleaseKeys")
            ?? throw new InvalidDataException("Missing embedded release verification keys.");
        var keys = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidDataException("Invalid release verification keys.");
        return new ReadOnlyDictionary<string, string>(keys);
    }
}
