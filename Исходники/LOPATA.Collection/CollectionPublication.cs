using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Lopata.Collection;

/// <summary>Rejects scan replies belonging to a superseded configuration.</summary>
public static class CollectionPublication
{
    public static string Revision(CollectionConfig config) => Convert.ToHexStringLower(
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(config, CollectionFiles.Json)));

    public static bool IsCurrent(string root, string revision, string? publisherExecutable = null)
    {
        try
        {
            var flag = File.ReadAllBytes(Path.Combine(root, "enabled.bin"));
            if (flag.Length != 4 || BitConverter.ToUInt32(flag) != 1) return false;
            var config = CollectionConfig.Load(root);
            return config.Enabled && Revision(config) == revision
                && (publisherExecutable is null || config.ExpectedWorkerPath.Length == 0
                    || Path.GetFullPath(config.ExpectedWorkerPath).Equals(Path.GetFullPath(publisherExecutable), StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return false;
        }
    }

    public static IReadOnlyList<CollectionItem> ReadCurrent(string root, string revision)
    {
        var stamp = Path.Combine(root, "snapshot.revision");
        try
        {
            if (!File.Exists(stamp) || new FileInfo(stamp).Length > 128 || File.ReadAllText(stamp) != revision) return [];
            return CollectionSnapshot.Read(root);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { return []; }
    }

    public static bool TryPublish(string root, string revision, IReadOnlyList<CollectionItem> items, string? publisherExecutable = null)
    {
        var published = false;
        WithLock(root, () =>
        {
            if (!IsCurrent(root, revision, publisherExecutable)) return;
            var stamp = Path.Combine(root, "snapshot.revision");
            var same = File.Exists(stamp) && new FileInfo(stamp).Length <= 128 && File.ReadAllText(stamp) == revision
                && File.Exists(Path.Combine(root, "snapshot.bin"));
            if (same)
            {
                try { same = CollectionSnapshot.Read(root).SequenceEqual(items); }
                catch (Exception error) when (error is IOException or InvalidDataException) { same = false; }
            }
            if (!same) CollectionSnapshot.Write(root, items);
            CollectionFiles.WriteAtomic(Path.Combine(root, "snapshot.revision"), Encoding.ASCII.GetBytes(revision));
            published = true;
        });
        return published;
    }

    public static IReadOnlyList<CollectionItem> MergeValidated(CollectionConfig config,
        IReadOnlyList<CollectionItem> current, IReadOnlyList<CollectionItem> previous, string? dataDirectory = null)
    {
        var items = current.ToDictionary(p => p.Path, StringComparer.OrdinalIgnoreCase);
        var bytes = 20 + current.Sum(p => 12 + 2 * (p.Path.Length + p.Kind.Length + p.Scenario.Length));
        foreach (var old in previous)
        {
            if (items.ContainsKey(old.Path) || !CollectionFilePolicy.IsAllowed(old.Path, config)) continue;
            try
            {
                var before = new FileInfo(old.Path);
                if (!before.Exists || !CollectionFilePolicy.CanRead(before)) continue;
                var size = before.Length; var modified = before.LastWriteTimeUtc.Ticks; var created = before.CreationTimeUtc.Ticks;
                var origin = CollectionOriginReader.Read(old.Path)
                    ?? (dataDirectory is null ? null : CollectionJournal.TryResolveOrigin(old.Path, dataDirectory));
                before.Refresh();
                if (origin is null || !before.Exists || before.Length != size || before.LastWriteTimeUtc.Ticks != modified || before.CreationTimeUtc.Ticks != created) continue;
                var item = new CollectionItem(old.Path, CollectionLabels.Kind(old.Path, config.Language), CollectionLabels.Scenario(origin.Scenario, config.Language));
                var next = 12 + 2 * (item.Path.Length + item.Kind.Length + item.Scenario.Length);
                if (items.Count >= CollectionSnapshot.MaxEntries || bytes + next > CollectionSnapshot.MaxBytes) break;
                items.Add(item.Path, item); bytes += next;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return items.Values.OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static IReadOnlyList<CollectionItem> FilterAvailable(CollectionConfig config,
        IReadOnlyList<CollectionItem> previous, CancellationToken token)
    {
        var available = new List<CollectionItem>(previous.Count);
        foreach (var item in previous)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (!CollectionFilePolicy.IsAllowed(item.Path, config)) continue;
                var file = new FileInfo(item.Path);
                if (file.Exists && CollectionFilePolicy.CanRead(file)) available.Add(item);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException) { }
        }
        return available;
    }

    internal static void WithLock(string root, Action action)
    {
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToUpperInvariant())))[..24];
        using var gate = new Mutex(false, @"Local\LOPATA.CollectionPublication." + identity);
        bool acquired;
        try { acquired = gate.WaitOne(TimeSpan.FromSeconds(2)); } catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new IOException("Collection publication is busy.");
        try { action(); } finally { gate.ReleaseMutex(); }
    }
}
