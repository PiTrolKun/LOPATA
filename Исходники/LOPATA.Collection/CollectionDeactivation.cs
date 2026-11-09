using System.Text.Json;

namespace Lopata.Collection;

public static class CollectionDeactivation
{
    /// <summary>Stops monitors first; malformed optional configuration is retained for diagnosis.</summary>
    public static string? Disable(string directory)
    {
        CollectionPublication.WithLock(directory, () =>
            CollectionFiles.WriteAtomic(Path.Combine(directory, "enabled.bin"), BitConverter.GetBytes(0U)));
        try
        {
            (CollectionConfig.Load(directory) with { Enabled = false }).Save(directory);
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        { return error.GetType().Name; }
    }
}
