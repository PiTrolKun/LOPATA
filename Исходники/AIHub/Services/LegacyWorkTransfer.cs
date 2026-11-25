using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Services.Shorts;
using AIHub.Services.SongClip;
using AIHub.Services.SpeechAssets;
using Lopata.Updates;

namespace AIHub.Services;

internal sealed record LegacyWorkItem(string Family, string Id, string Source, string Destination, long Bytes);

internal sealed class LegacyWorkTransfer(ManagedWorkStorage storage)
{
    private static readonly string[] Families = ["SongClip/Projects", "Shorts/Jobs", "SpeechAssets/Sessions"];
    private sealed record MoveState(string Source, string Destination, string StagingId);

    internal IReadOnlyList<LegacyWorkItem> Plan()
    {
        if (storage.ConfiguredRoot is null) return [];
        var plan = new List<LegacyWorkItem>();
        foreach (var family in Families)
            foreach (var id in storage.Jobs(family))
            {
                try
                {
                    var source = storage.LegacyJob(family, id);
                    if (!Directory.Exists(source)) continue;
                    var outputDirectory = Inspect(family, id, source);
                    if (outputDirectory == "") continue;
                    var destination = storage.Destination(family, id, temporary: false, outputDirectory);
                    if (source.Equals(destination, StringComparison.OrdinalIgnoreCase)) continue;
                    if (destination.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        continue;
                    plan.Add(new(family, id, source, destination, Inventory(source).Sum(file => file.Length)));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
                {
                    OwnedProcessRegistry.Log("legacy_work_not_movable", family, detail: error.GetType().Name);
                }
            }
        return plan;
    }

    private static string? Inspect(string family, string id, string source)
    {
        if (family == "SongClip/Projects")
        {
            var project = new SongClipStore(Path.GetDirectoryName(source)).Load(id);
            return project.PendingAttemptId.Length == 0 ? null : "";
        }
        if (family == "SpeechAssets/Sessions")
        {
            var session = JsonSerializer.Deserialize<SpeechAssetSession>(File.ReadAllText(Path.Combine(source, "session.json")));
            if (session?.Id != id) return "";
            new SpeechAssetStore(Path.GetDirectoryName(source)).Validate(session);
            return session.Persistent && session.Runs.All(run => run.State is "completed" or "canceled" or "failed") ? null : "";
        }
        var job = JsonSerializer.Deserialize<ShortsJob>(File.ReadAllText(Path.Combine(source, "shorts.json")));
        if (job?.Id != id || !Path.IsPathFullyQualified(job.Output) || !File.Exists(job.Output)) return "";
        if (job.Description is not null) return ""; // Historic descriptions retain absolute H3 input paths.
        return Path.GetDirectoryName(job.Output);
    }

    internal async Task<long> MoveAsync(LegacyWorkItem item, CancellationToken token)
    {
        if (!Families.Contains(item.Family) || storage.LegacyJob(item.Family, item.Id) != item.Source)
            throw new InvalidDataException("The storage transfer proposal changed.");
        using var gate = ModelStorageWorkGate.Current.TryBeginTransfer() ?? throw new IOException("Storage is in use.");
        var outputDirectory = Inspect(item.Family, item.Id, item.Source);
        if (outputDirectory == "" || storage.Destination(item.Family, item.Id, false, outputDirectory) != item.Destination)
            throw new IOException("The storage transfer proposal changed.");
        var receipt = storage.MoveReceipt(item.Family, item.Id);
        if (!File.Exists(receipt))
            SongClipStore.Atomic(receipt, new MoveState(item.Source, item.Destination, Guid.NewGuid().ToString("N")));
        var state = JsonSerializer.Deserialize<MoveState>(File.ReadAllText(receipt));
        if (state is null || state.Source != item.Source || state.Destination != item.Destination
            || !Guid.TryParseExact(state.StagingId, "N", out _)) throw new IOException("The storage transfer receipt has changed.");
        var files = Inventory(item.Source);
        if (files.Sum(file => file.Length) != item.Bytes) throw new IOException("The project changed. Review the transfer again.");
        if (!Directory.Exists(item.Destination))
        {
            var size = new DriveInfo(Path.GetPathRoot(item.Destination)!).AvailableFreeSpace;
            if (size < item.Bytes + 16L * 1024 * 1024) throw new IOException("Insufficient disk space: " + item.Destination);
        }
        var staging = item.Destination + ".moving-" + state.StagingId;
        SafeUpdatePath.RejectLinks(staging);
        if (!Directory.Exists(item.Destination))
        {
            Directory.CreateDirectory(staging);
            foreach (var source in files)
            {
                token.ThrowIfCancellationRequested();
                var target = RuntimeFileAudit.Contained(staging, Path.GetRelativePath(item.Source, source.FullName));
                if (File.Exists(target))
                {
                    if (await Match(source.FullName, target, token)) continue;
                    File.Delete(target);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var input = File.OpenRead(source.FullName);
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
                await input.CopyToAsync(output, token);
                output.Flush(true);
            }
            if (Inventory(staging).Length != files.Length) throw new IOException("The transfer folder contains additional files.");
        }
        var copy = Directory.Exists(item.Destination) ? item.Destination : staging;
        foreach (var source in files.OrderBy(file => file.Name is "project.json" or "shorts.json" or "session.json"))
        {
            var counterpart = RuntimeFileAudit.Contained(copy, Path.GetRelativePath(item.Source, source.FullName));
            if (!await Match(source.FullName, counterpart, token)) throw new IOException("Could not verify transferred file: " + source.Name);
        }
        if (Inventory(item.Source).Length != files.Length) throw new IOException("The project changed during transfer.");
        if (copy == staging) Directory.Move(staging, item.Destination);
        storage.RememberMigration(item.Family, item.Id, item.Destination);
        if (storage.BindMigrated(item.Family, item.Id, item.Destination) != item.Destination)
            throw new IOException("Another address is already registered for the project.");
        // The address is durable before cleanup. After interruption the checked copy remains usable.
        long released = 0;
        foreach (var source in files)
        {
            var target = RuntimeFileAudit.Contained(item.Destination, Path.GetRelativePath(item.Source, source.FullName));
            if (!await Match(source.FullName, target, token)) continue;
            File.Delete(source.FullName);
            released += source.Length;
        }
        File.Delete(receipt);
        return released;
    }

    private static FileInfo[] Inventory(string path)
    {
        SafeUpdatePath.RejectLinks(path);
        var items = new List<FileInfo>();
        foreach (var item in new DirectoryInfo(path).EnumerateFileSystemInfos("*", new EnumerationOptions
        { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = 0 }))
        {
            SafeUpdatePath.RejectLinks(item.FullName);
            if (item is FileInfo file) items.Add(file);
        }
        return [.. items];
    }

    private static async Task<bool> Match(string left, string right, CancellationToken token)
    {
        if (!File.Exists(right)) return false;
        await using var before = File.OpenRead(left);
        await using var after = File.OpenRead(right);
        if (before.Length != after.Length) return false;
        var expected = await SHA256.HashDataAsync(before, token);
        var actual = await SHA256.HashDataAsync(after, token);
        return expected.SequenceEqual(actual);
    }
}
