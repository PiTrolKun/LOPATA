using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

internal sealed record ModelStorageAddress(string ArtifactId, string Revision, string ModelsRoot,
    string Directory, string[] PreviousDirectories);

/// <summary>All addresses in a successful move become visible together.</summary>
internal sealed class ModelStorageAddresses(string libraryDirectory)
{
    internal static ModelStorageAddresses Current { get; } = new(AppDataPaths.ManagedModelLibraryDirectory);
    internal static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
    private string CatalogPath => Path.Combine(libraryDirectory, "model-addresses.json");
    internal string JournalPath => Path.Combine(libraryDirectory, "model-transfer.json");

    internal ModelStorageAddress[] Read() => File.Exists(CatalogPath)
        ? ReadDocument<ModelStorageAddress[]>(CatalogPath) : [];

    internal ManagedModelArtifactCard Resolve(ManagedModelArtifactCard card) => Apply(card, Read());

    internal static ManagedModelArtifactCard Apply(ManagedModelArtifactCard card, ModelStorageAddress[] addresses)
    {
        var address = addresses.FirstOrDefault(item => Matches(item, card.ModelArtifactId, card.Revision));
        if (address is null || ManagedModelPathIdentity.SameDirectory(card.InstallDirectory, address.Directory)) return card;
        card.InstallDirectory = address.Directory;
        card.ModelsRoot = address.ModelsRoot;
        card.RuntimeVerifiedAt = null;
        foreach (var file in card.Files)
        {
            file.VerifiedSizeBytes = 0;
            file.VerifiedLastWriteTimeUtc = null;
        }
        return card;
    }

    internal string ResolvePreviousPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return path;
        var absolute = Path.GetFullPath(path);
        foreach (var address in Read())
        foreach (var oldDirectory in address.PreviousDirectories)
        {
            if (Paths.Equals(absolute, oldDirectory)) return address.Directory;
            if (IsBelow(oldDirectory, absolute)) return Path.Combine(address.Directory, Path.GetRelativePath(oldDirectory, absolute));
        }
        return path;
    }

    internal void Publish(ModelStorageAddress[] addresses) => WriteDocument(CatalogPath, addresses);

    internal static bool Matches(ModelStorageAddress address, string id, string revision) =>
        Paths.Equals(address.ArtifactId, id) && Paths.Equals(address.Revision, revision);

    internal static bool IsBelow(string root, string path)
    {
        var prefix = Path.GetFullPath(root);
        if (!Path.EndsInDirectorySeparator(prefix)) prefix += Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        return fullPath.Length > prefix.Length && fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    internal static T ReadDocument<T>(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return JsonSerializer.Deserialize<T>(stream) ?? throw new InvalidDataException("Invalid model storage record.");
    }

    internal static void WriteDocument<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
