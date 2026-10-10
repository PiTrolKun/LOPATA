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
        using var document = JsonDocument.Parse(stream);
        var keys = document.RootElement.EnumerateObject().ToDictionary(p => p.Name,
            p => p.Value.GetString() ?? throw new InvalidDataException("Invalid release verification key."));
        return new ReadOnlyDictionary<string, string>(keys);
    }
}
