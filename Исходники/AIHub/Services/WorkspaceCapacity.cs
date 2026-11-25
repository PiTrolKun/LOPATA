using System.Globalization;
using System.IO;
using AIHub.Models;
using Lopata.Updates;

namespace AIHub.Services;

internal static class WorkspaceCapacity
{
    private const double GiB = 1073741824d;
    private sealed record Reservation(string Folder, long Bytes);
    private sealed class Lease(FileStream stream) : IDisposable
    {
        public void Dispose()
        {
            var path = stream.Name;
            stream.Dispose();
            try { File.Delete(path); }
            catch (IOException) { /* Reclaimed by the next reservation. */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static IDisposable Reserve(string profile, StorageCategorySettings settings, string folder, long bytes, bool temporary)
    {
        if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var directory = Path.Combine(profile, "StorageReservations");
        SafeUpdatePath.RejectLinks(directory);
        Directory.CreateDirectory(directory);
        using var mutex = new Mutex(false, "Local\\LOPATA-Storage-" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(profile.ToUpperInvariant())))[..24]);
        try { if (!mutex.WaitOne(TimeSpan.FromSeconds(20))) throw new IOException("Storage catalogue is busy."); }
        catch (AbandonedMutexException) { /* Windows transferred ownership. */ }
        try
        {
            var active = new List<Reservation>();
            foreach (var path in Directory.EnumerateFiles(directory, "*.reservation"))
            {
                SafeUpdatePath.RejectLinks(path);
                try
                {
                    using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) { }
                    File.Delete(path);
                }
                catch (FileNotFoundException) { }
                catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
                {
                    try
                    {
                        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        var reservation = System.Text.Json.JsonSerializer.Deserialize<Reservation>(stream);
                        if (reservation is not null) active.Add(reservation);
                    }
                    catch (FileNotFoundException) { }
                }
            }
            Check(settings, folder, bytes, temporary, active);
            var file = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".reservation");
            using (var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                System.Text.Json.JsonSerializer.Serialize(output, new Reservation(folder, bytes));
            return new Lease(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read));
        }
        finally { mutex.ReleaseMutex(); }
    }

    private static void Check(StorageCategorySettings settings, string folder, long bytes, bool temporary, List<Reservation> active)
    {
        var drive = Path.GetPathRoot(folder)!;
        const long slack = 16L * 1024 * 1024;
        var disk = new DriveInfo(drive);
        var needed = checked(bytes + slack + active.Where(item =>
            string.Equals(Path.GetPathRoot(item.Folder), drive, StringComparison.OrdinalIgnoreCase)).Sum(item => item.Bytes));
        if (!disk.IsReady || disk.AvailableFreeSpace < needed)
            throw new IOException("Insufficient free storage: " + folder + " (" + needed.ToString(CultureInfo.InvariantCulture) + " bytes required).");

        var locations = settings.Locations.Where(location => Path.IsPathFullyQualified(location.Path))
            .Select(x => (Path: Path.TrimEndingDirectorySeparator(Path.GetFullPath(x.Path)), x.LimitGb)).ToArray();
        var location = locations.Where(item => Contains(folder, item.Path)).OrderByDescending(item => item.Path.Length).FirstOrDefault();
        if (location.Path is null) return;
        var overflow = temporary && settings.AllowTemporaryOverflow ? Math.Max(0, settings.TemporaryOverflowGb) : 0;
        if (location.LimitGb > 0 && Sum(location.Path) + active.Where(x => Contains(x.Folder, location.Path)).Sum(x => x.Bytes) + bytes
            > (location.LimitGb + overflow) * GiB) throw new IOException("Storage directory limit exceeded: " + location.Path);
        if (settings.TotalLimitGb <= 0) return;
        var roots = locations.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var managed = roots.Where(root => !roots.Any(other => !root.Equals(other, StringComparison.OrdinalIgnoreCase)
            && Contains(root, other))).ToArray();
        if (managed.Sum(Sum) + active.Where(x => managed.Any(root => Contains(x.Folder, root))).Sum(x => x.Bytes) + bytes
            > (settings.TotalLimitGb + overflow) * GiB) throw new IOException("Total workspace storage limit exceeded.");
    }

    private static bool Contains(string path, string root)
    {
        var boundary = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(boundary, StringComparison.OrdinalIgnoreCase);
    }

    private static long Sum(string path)
    {
        if (!Directory.Exists(path)) return 0;
        SafeUpdatePath.RejectLinks(path);
        if (Path.GetPathRoot(path)?.Equals(path, StringComparison.OrdinalIgnoreCase) == true)
        {
            var volume = new DriveInfo(path);
            return volume.TotalSize - volume.TotalFreeSpace;
        }
        return new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions
        { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint }).Sum(f => f.Length);
    }
}
