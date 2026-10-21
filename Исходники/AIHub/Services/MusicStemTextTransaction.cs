using System.IO;
using System.Text;
using System.Text.Json;

namespace AIHub.Services;

/// <summary>Commit a bundle TXT and its receipt together, including recovery after an atomic replacement.</summary>
internal static class MusicStemTextTransaction
{
    private sealed record Pending(string JobId, int Index, string Nonce, string? Before, string After);
    private static string Journal(MusicGenerationJobs jobs, string id, int index) => Path.Combine(jobs.Folder(id), "stems-text-" + index + ".transaction.json");
    internal static async Task PublishAsync(MusicGenerationJobs jobs, string id, int index, string content, CancellationToken token)
    {
        var job = jobs.Load(id); var v = job.Variants[index]; var destination = MusicStemBundle.TextPath(v);
        if (File.Exists(destination)) {
            if (v.Stems?.TextHash is null) throw new IOException("An unrelated TXT occupies the song folder.");
            await MusicOutputPublisher.MatchHashAsync(destination, v.Stems.TextHash, token);
        }
        var nonce = Guid.NewGuid().ToString("N"); var stage = destination + ".text-" + nonce + ".tmp";
        var journal = Journal(jobs, id, index);
        var journalStage = journal + "." + nonce + ".tmp";
        try {
            await File.WriteAllTextAsync(stage, content, new UTF8Encoding(false), token);
            var after = await MusicOutputPublisher.HashAsync(stage, token);
            if (v.Stems!.Published && after == v.Stems.TextHash) return;
            var pending = new Pending(id, index, nonce, File.Exists(destination) ? v.Stems.TextHash : null, after);
            token.ThrowIfCancellationRequested();
            lock (MusicLyricsTimingStore.Gate(jobs.Folder(id))) {
                var latest = jobs.Load(id);
                if (latest.Variants[index].Stems!.TextHash != v.Stems.TextHash || File.Exists(journal)) throw new IOException("Another TXT transaction changed the bundle.");
                using (var output = new FileStream(journalStage, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                    JsonSerializer.Serialize(output, pending); output.Flush(true);
                }
                File.Move(journalStage, journal, false);
                Recover(jobs, latest);
            }
        }
        finally { if (!File.Exists(journal) && File.Exists(stage)) File.Delete(stage); if (File.Exists(journalStage)) File.Delete(journalStage); }
    }
    internal static MusicGenerationJob Recover(MusicGenerationJobs jobs, MusicGenerationJob job)
    {
        for (var index = 0; index < job.Variants.Length; index++) {
            var journal = Journal(jobs, job.Id, index); if (!File.Exists(journal)) continue;
            var v = job.Variants[index];
            var p = JsonSerializer.Deserialize<Pending>(File.ReadAllText(journal)) ?? throw new InvalidDataException("Empty bundle TXT transaction.");
            if (!job.SeparateTracks || v.Stems is null || !v.Completed || v.KaraokeReadiness == AIHub.Models.MusicKaraokeReadiness.Pending ||
                p.JobId != job.Id || p.Index != index || !Guid.TryParseExact(p.Nonce, "N", out _) || p.After.Length != 64 || !p.After.All(Uri.IsHexDigit) ||
                v.Stems.TextHash != p.Before && v.Stems.TextHash != p.After)
                throw new InvalidDataException("Bundle TXT transaction identity mismatch.");
            var destination = MusicStemBundle.TextPath(v); var stage = destination + ".text-" + p.Nonce + ".tmp";
            var actual = File.Exists(destination) ? Hash(destination) : null;
            if (actual != p.After) {
                if (actual != p.Before || !File.Exists(stage) || Hash(stage) != p.After) throw new InvalidDataException("Bundle TXT transaction content mismatch.");
                File.Move(stage, destination, p.Before is not null);
            }
            var variants = job.Variants.ToArray(); variants[index] = v with { Stems = v.Stems with { TextHash = p.After, Published = true, Error = null } };
            job = job with { Variants = variants }; jobs.Save(job);
            if (File.Exists(stage)) File.Delete(stage); File.Delete(journal);
        }
        return job;
    }
    private static string Hash(string path) {
        using var stream = File.OpenRead(path); return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }
}
