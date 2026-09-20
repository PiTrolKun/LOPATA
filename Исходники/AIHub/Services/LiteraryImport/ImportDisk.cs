using System.IO;

namespace AIHub.Services.LiteraryImport;

public static class ImportDisk
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, (DateTime At, long Bytes)> Usage = new(StringComparer.OrdinalIgnoreCase);
    public static void Require(string folder, long bytes)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(folder))!;
        if (new DriveInfo(root).AvailableFreeSpace < checked(bytes + 64L * 1024 * 1024))
            throw new IOException("Literary.Import.DiskFull");
        var settings = new StorageSettingsStore().LoadOrCreate().Results;
        var locations = settings.Locations.Where(l => !string.IsNullOrWhiteSpace(l.Path)).ToArray();
        var location = locations.Where(l => Within(folder, l.Path)).OrderByDescending(l => l.Path.Length).FirstOrDefault();
        if (location is null) return; // Explicit folders outside managed storage use their physical disk limit.
        var overflow = settings.AllowTemporaryOverflow ? settings.TemporaryOverflowGb : 0;
        if (location.LimitGb > 0 && Size(location.Path) + bytes > (location.LimitGb + overflow) * 1073741824d)
            throw new IOException("Literary.Import.Quota");
        var roots = locations.Select(l => Path.GetFullPath(l.Path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var independent = roots.Where(p => !roots.Any(other => p != other && Within(p, other)));
        if (settings.TotalLimitGb > 0 && independent.Sum(Size) + bytes > (settings.TotalLimitGb + overflow) * 1073741824d)
            throw new IOException("Literary.Import.Quota");
    }
    private static bool Within(string path, string parent) => string.Equals(Path.GetFullPath(path).TrimEnd('\\'), Path.GetFullPath(parent).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
        || Path.GetFullPath(path).StartsWith(Path.GetFullPath(parent).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    private static long Size(string folder)
    {
        lock (Sync)
        {
            if (Usage.TryGetValue(folder, out var cached) && DateTime.UtcNow - cached.At < TimeSpan.FromSeconds(5)) return cached.Bytes;
            if (!Directory.Exists(folder)) return 0;
            var value = new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true,
                IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint }).Sum(f => f.Length);
            Usage[folder] = (DateTime.UtcNow, value); return value;
        }
    }
}
