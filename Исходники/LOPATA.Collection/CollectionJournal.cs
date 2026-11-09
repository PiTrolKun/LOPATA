using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Lopata.Collection;

public sealed record CollectionJournalEntry(string Path, string Scenario, long Length, long ModifiedUtcTicks, long CreatedUtcTicks, string? ContentSha256 = null);

/// <summary>Completed output addresses. Every candidate is revalidated before it enters a view.</summary>
public static class CollectionJournal
{
    public static bool RecordCompleted(string path, string scenario, string? dataDirectory = null, string? contentSha256 = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || scenario is null || contentSha256 is not null && !CollectionContentDigest.IsValid(contentSha256)) return false;
            path = Path.GetFullPath(path);
            var file = new FileInfo(path);
            if (!file.Exists || !CollectionFilePolicy.CanRead(file) || scenario.Length > 256 || scenario.Any(char.IsControl)) return false;
            var entry = new CollectionJournalEntry(path, scenario, file.Length, file.LastWriteTimeUtc.Ticks, file.CreationTimeUtc.Ticks, contentSha256?.ToLowerInvariant());
            var root = dataDirectory ?? CollectionPaths.DefaultDirectory;
            var id = AddressId(path);
            CollectionFiles.WriteAtomic(Path.Combine(root, "journal", id + ".json"), JsonSerializer.SerializeToUtf8Bytes(entry, CollectionFiles.Json));
            CollectionFiles.WriteAtomic(Path.Combine(root, "refresh.request"), Encoding.ASCII.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()));
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            return false;
        }
    }

    public static IEnumerable<CollectionJournalEntry> Read(string directory)
    {
        var folder = Path.Combine(directory, "journal");
        if (!Directory.Exists(folder)) yield break;
        foreach (var path in Directory.EnumerateFiles(folder, "*.json", SearchOption.TopDirectoryOnly).Take(CollectionSnapshot.MaxEntries))
        {
            CollectionJournalEntry? entry = null;
            try
            {
                if (new FileInfo(path).Length <= 128 * 1024)
                    entry = JsonSerializer.Deserialize<CollectionJournalEntry>(File.ReadAllText(path), CollectionFiles.Json);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
            if (Valid(entry)) yield return entry!;
        }
    }

    /// <summary>A local receipt admits only the exact completed primary bytes, never an address alone.</summary>
    public static CollectionOrigin? TryResolveOrigin(string path, string dataDirectory)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path) || !Path.IsPathFullyQualified(dataDirectory)) return null;
            path = Path.GetFullPath(path);
            var receipt = Path.Combine(dataDirectory, "journal", AddressId(path) + ".json");
            if (!File.Exists(receipt) || new FileInfo(receipt).Length > 128 * 1024) return null;
            var entry = JsonSerializer.Deserialize<CollectionJournalEntry>(File.ReadAllText(receipt), CollectionFiles.Json);
            if (!Valid(entry) || entry!.ContentSha256 is null || !entry.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) return null;
            var stamp = new CollectionContentStamp(entry.Length, entry.ModifiedUtcTicks, entry.CreatedUtcTicks);
            return CollectionContentDigest.Matches(path, entry.Length, entry.ContentSha256, stamp)
                ? new(entry.Scenario, RequiresContentValidation: true) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or JsonException or System.Security.SecurityException) { return null; }
    }

    private static bool Valid(CollectionJournalEntry? entry) => entry is not null
        && entry.Path is { Length: > 0 and <= 32700 } && Path.IsPathFullyQualified(entry.Path) && !entry.Path.Any(char.IsControl)
        && entry.Scenario is { Length: <= 256 } && !entry.Scenario.Any(char.IsControl) && entry.Length >= 0
        && (entry.ContentSha256 is null || CollectionContentDigest.IsValid(entry.ContentSha256));

    private static string AddressId(string path) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
}

public static class CollectionFilePolicy
{
    private const FileAttributes RecallOnOpen = (FileAttributes)0x40000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x400000;

    public static bool CanRead(FileSystemInfo file) =>
        (file.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) == 0;

    public static bool IsWithin(string path, string root)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        path = Path.GetFullPath(path);
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return path.Equals(root, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAllowed(string path, CollectionConfig config)
    {
        if (!Path.IsPathFullyQualified(path) || path.Any(char.IsControl) || (!config.AllowNetwork && path.StartsWith(@"\\", StringComparison.Ordinal))) return false;
        var name = Path.GetFileName(path);
        if (name.Length == 44 && name.StartsWith(".lopata-", StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) && name[8..40].All(Uri.IsHexDigit)) return false;
        return !config.ExcludedRoots.Any(root => IsWithin(path, root));
    }
}
