using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Durable timing cache and recoverable late tag transaction. Never relaxes existing file receipts.</summary>
public sealed class MusicLyricsTimingStore(MusicGenerationJobs jobs)
{
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private sealed record Pending(string JobId, int Variant, bool Additional, string OriginalHash, string NewHash, string TemporaryName, string BackupName);
    internal static object Gate(string folder) => Gates.GetOrAdd(Path.GetFullPath(folder), _ => new());
    private string CachePath(string id, int index, bool additional = false) => Path.Combine(jobs.Folder(id), "lyrics-timing-" + index + (additional ? "-additional" : "") + ".json");

    public async Task<MusicTimedLyrics?> ReadAsync(MusicGenerationJob job, int index, CancellationToken token)
        => await ReadCoreAsync(job, index, false, token);
    public async Task<MusicTimedLyrics?> ReadAdditionalAsync(MusicGenerationJob job, int index, CancellationToken token)
        => await ReadCoreAsync(job, index, true, token);
    private async Task<MusicTimedLyrics?> ReadCoreAsync(MusicGenerationJob job, int index, bool additional, CancellationToken token)
    {
        if (index < 0 || index >= job.Variants.Length) throw new ArgumentOutOfRangeException(nameof(index));
        var variant = job.Variants[index]; var audioPath = additional ? variant.AdditionalPath : variant.ResultPath;
        if (!variant.Completed || audioPath is null || !File.Exists(audioPath)) return null;
        var path = CachePath(job.Id, index, additional); MusicTimedLyrics? value;
        try {
            var receipt = additional ? variant.AdditionalHash : variant.ResultHash ?? (job.Output is null ? variant.AudioHash : null);
            if (receipt is null) return null;
            await MusicOutputPublisher.MatchHashAsync(audioPath, receipt, token);
            value = File.Exists(path) ? JsonSerializer.Deserialize<MusicTimedLyrics>(await File.ReadAllTextAsync(path, token))
                : await MusicLyricsTimingMetadata.ReadAsync(audioPath, token);
            if (value is null) return null;
            Match(job, index, value); value.Validate();
            if (!value.AudioPayloadHash.Equals(await MusicLyricsTimingMetadata.AudioPayloadHashAsync(audioPath, token), StringComparison.OrdinalIgnoreCase)) return null;
            if (additional && (value.Clock is null || value.Clock.SourcePayloadHash !=
                    await MusicLyricsTimingMetadata.AudioPayloadHashAsync(variant.ResultPath, token))) return null;
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException or ArgumentException) { return null; }
        // Cache persistence is independent of portable tags. A prior cancellation may have happened before a journal existed.
        // On the next normal selection retry the missing metadata; never require a manual sync action.
        try {
            var embedded = await MusicLyricsTimingMetadata.ReadAsync(audioPath, token);
            if (!File.Exists(JournalPath(jobs.Folder(job.Id), index, additional)) &&
                (embedded is null || JsonSerializer.Serialize(embedded) != JsonSerializer.Serialize(value)))
                await PublishCoreAsync(job.Id, index, value, additional, token);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException or UnauthorizedAccessException) { }
        return value;
    }
    public async Task PublishAsync(string jobId, int index, MusicTimedLyrics timing, CancellationToken token)
        => await PublishCoreAsync(jobId, index, timing, false, token);
    public async Task PublishAdditionalAsync(string jobId, int index, MusicTimedLyrics timing, CancellationToken token)
        => await PublishCoreAsync(jobId, index, timing, true, token);
    private async Task PublishCoreAsync(string jobId, int index, MusicTimedLyrics timing, bool additional, CancellationToken token)
    {
        var job = jobs.Load(jobId); if (index < 0 || index >= job.Variants.Length) throw new ArgumentOutOfRangeException(nameof(index));
        Match(job, index, timing); timing.Validate(); var variant = job.Variants[index];
        if (!variant.Completed) throw new InvalidDataException("Cannot attach timing before audio completion.");
        var audioPath = additional ? variant.AdditionalPath : variant.ResultPath;
        if (audioPath is null) throw new InvalidDataException("No additional audio output.");
        if (additional && (timing.Clock is null || timing.Clock.SourcePayloadHash !=
                await MusicLyricsTimingMetadata.AudioPayloadHashAsync(variant.ResultPath, token)))
            throw new InvalidDataException("Additional timed lyrics require a verified source clock.");
        var known = (additional ? variant.AdditionalHash : variant.ResultHash ?? (job.Output is null ? variant.AudioHash : null))
            ?? throw new InvalidDataException("Missing original music receipt.");
        await MusicOutputPublisher.MatchHashAsync(audioPath, known, token);
        if (!timing.AudioPayloadHash.Equals(await MusicLyricsTimingMetadata.AudioPayloadHashAsync(audioPath, token), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Timing cache audio identity mismatch.");
        await AtomicJsonAsync(CachePath(job.Id, index, additional), timing, token);
        await PublishMetadataCopyAsync(jobId, index, additional,
            (source, destination, cancellation) => MusicLyricsTimingMetadata.WriteCopyAsync(source, destination, timing, cancellation), token);
    }
    internal async Task PublishMetadataCopyAsync(string jobId, int index, bool additional,
        Func<string, string, CancellationToken, Task> writeCopy, CancellationToken token)
    {
        var job = jobs.Load(jobId);
        if (index < 0 || index >= job.Variants.Length) throw new ArgumentOutOfRangeException(nameof(index));
        var variant = job.Variants[index];
        if (!variant.Completed) throw new InvalidDataException("Cannot attach metadata before audio completion.");
        var audioPath = (additional ? variant.AdditionalPath : variant.ResultPath) ?? throw new InvalidDataException("No audio output.");
        var known = (additional ? variant.AdditionalHash : variant.ResultHash ?? (job.Output is null ? variant.AudioHash : null))
            ?? throw new InvalidDataException("Missing original music receipt.");
        await MusicOutputPublisher.MatchHashAsync(audioPath, known, token);
        await Stage(audioPath, known);
        async Task Stage(string path, string hash)
        {
            var folder = jobs.Folder(job.Id); var journal = JournalPath(folder, index, additional);
            if (File.Exists(journal)) { lock (Gate(folder)) Recover(jobs, jobs.Load(job.Id)); return; }
            var nonce = Guid.NewGuid().ToString("N"); var temporary = path + ".timing-" + nonce + ".tmp";
            var backup = path + ".timing-" + nonce + ".bak";
            try {
                await writeCopy(path, temporary, token);
                var newHash = await MusicOutputPublisher.HashAsync(temporary, token);
                await MusicOutputPublisher.MatchHashAsync(path, hash, token);
                var pending = new Pending(job.Id, index, additional, hash, newHash, Path.GetFileName(temporary), Path.GetFileName(backup));
                token.ThrowIfCancellationRequested();
                lock (Gate(folder)) {
                    if (File.Exists(journal)) throw new IOException("Another timed metadata transaction is pending.");
                    AtomicJson(journal, pending);
                    // All later cleanup is recovery-owned: a crash must retain the stage and original backup.
                    Recover(jobs, jobs.Load(job.Id));
                }
            }
            finally { if (!File.Exists(journal) && File.Exists(temporary)) File.Delete(temporary); }
        }
    }
    private static void Match(MusicGenerationJob job, int index, MusicTimedLyrics value)
    {
        if (value.JobId != job.Id || value.Variant != index || value.ProjectId != job.ProjectId || value.ProjectStep != job.ProjectStep ||
            value.SourceLyrics != MusicTimedLyrics.Normalize(job.Lyrics) || value.LyricsHash != MusicTimedLyrics.HashLyrics(job.Lyrics))
            throw new InvalidDataException("Timed lyrics job/snapshot mismatch.");
    }
    private static string JournalPath(string folder, int index, bool additional) => Path.Combine(folder, "lyrics-timing-" + index + (additional ? "-additional" : "") + ".transaction.json");
    /// <summary>Called while loading the validated job; paths derive from that job, never from untrusted journal paths.</summary>
    internal static MusicGenerationJob Recover(MusicGenerationJobs jobs, MusicGenerationJob job)
    {
        try { return RecoverCore(jobs, job); }
        catch (Exception ex) when (ex is InvalidDataException or JsonException) {
            // A broken karaoke journal must not make a checksum-valid completed song inaccessible.
            // If a file is ahead of the receipt, however, retain the failure rather than trusting unknown bytes.
            foreach (var variant in job.Variants.Where(v => v.Completed)) {
                var primary = variant.ResultHash ?? (job.Output is null ? variant.AudioHash : null);
                if (primary is null || Hash(variant.ResultPath) != primary || variant.AdditionalPath is { } additional &&
                    (variant.AdditionalHash is null || Hash(additional) != variant.AdditionalHash)) throw;
            }
            var folder = jobs.Folder(job.Id);
            lock (Gate(folder)) foreach (var journal in Directory.GetFiles(folder, "lyrics-timing-*.transaction.json")) {
                try { File.Move(journal, journal + ".invalid-" + Guid.NewGuid().ToString("N")); }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
            }
            try { File.WriteAllText(Path.Combine(folder, "lyrics-timing-recovery-error.txt"), ex.ToString()); }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
            return job;
        }
    }
    private static MusicGenerationJob RecoverCore(MusicGenerationJobs jobs, MusicGenerationJob job)
    {
        var folder = jobs.Folder(job.Id);
        lock (Gate(folder)) {
            for (var index = 0; index < job.Variants.Length; index++) foreach (var additional in new[] { false, true }) {
                var journal = JournalPath(folder, index, additional); if (!File.Exists(journal)) continue;
                var pending = JsonSerializer.Deserialize<Pending>(File.ReadAllText(journal)) ?? throw new InvalidDataException("Empty timed metadata transaction.");
                var variant = job.Variants[index]; var path = additional ? variant.AdditionalPath : variant.ResultPath;
                var expected = additional ? variant.AdditionalHash : variant.ResultHash ?? (job.Output is null ? variant.AudioHash : null);
                if (pending.JobId != job.Id || pending.Variant != index || pending.Additional != additional || path is null ||
                    pending.TemporaryName is null || pending.BackupName is null || pending.OriginalHash is null || pending.NewHash is null ||
                    pending.TemporaryName != Path.GetFileName(pending.TemporaryName) || pending.BackupName != Path.GetFileName(pending.BackupName) ||
                    !pending.TemporaryName.StartsWith(Path.GetFileName(path) + ".timing-", StringComparison.Ordinal) || !pending.TemporaryName.EndsWith(".tmp", StringComparison.Ordinal) ||
                    !pending.BackupName.StartsWith(Path.GetFileName(path) + ".timing-", StringComparison.Ordinal) || !pending.BackupName.EndsWith(".bak", StringComparison.Ordinal) ||
                    expected is null || !expected.Equals(pending.OriginalHash, StringComparison.OrdinalIgnoreCase) && !expected.Equals(pending.NewHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Timed metadata transaction identity mismatch.");
                var temporary = Path.Combine(Path.GetDirectoryName(path)!, pending.TemporaryName); var backup = Path.Combine(Path.GetDirectoryName(path)!, pending.BackupName);
                var prefix = Path.GetFileName(path) + ".timing-";
                var nonce = pending.TemporaryName[prefix.Length..^4];
                if (!Guid.TryParseExact(nonce, "N", out _) || pending.BackupName != prefix + nonce + ".bak" ||
                    File.Exists(backup) && Hash(backup) != pending.OriginalHash)
                    throw new InvalidDataException("Timed metadata backup identity mismatch.");
                var actual = Hash(path);
                if (actual.Equals(pending.OriginalHash, StringComparison.OrdinalIgnoreCase)) {
                    if (!File.Exists(temporary) || Hash(temporary) != pending.NewHash) throw new InvalidDataException("Missing timed metadata stage.");
                    try { File.Replace(temporary, path, backup); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; } // Player/read-only lock: retry without touching audio.
                    actual = Hash(path);
                }
                if (!actual.Equals(pending.NewHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Timed metadata receipt mismatch.");
                var variants = job.Variants.ToArray(); variants[index] = additional ? variant with { AdditionalHash = pending.NewHash } : variant with { ResultHash = pending.NewHash };
                job = job with { Variants = variants };
                try { jobs.Save(job); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return job; } // Keep journal; in-memory receipt matches the already committed file.
                File.Delete(journal); if (File.Exists(temporary)) File.Delete(temporary);
                if (File.Exists(backup) && Hash(backup) == pending.OriginalHash) File.Delete(backup);
            }
            return job;
        }
    }
    private static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file)); }
    private static void AtomicJson<T>(string path, T value)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { JsonSerializer.Serialize(file, value); file.Flush(true); } File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static async Task AtomicJsonAsync<T>(string path, T value, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { await JsonSerializer.SerializeAsync(file, value, cancellationToken: token); file.Flush(true); }
            token.ThrowIfCancellationRequested(); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
