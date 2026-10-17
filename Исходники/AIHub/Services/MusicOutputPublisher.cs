using System.IO;
using System.Security.Cryptography;

namespace AIHub.Services;

/// <summary>Every encoding starts from native PCM. Hashes are checkpointed before publishing either file.</summary>
public sealed class MusicOutputPublisher(IMusicAudioEncoder encoder)
{
    public static Task ApplyTimedLyricsAsync(MusicGenerationJobs jobs, string jobId, int variant,
        AIHub.Models.MusicTimedLyrics timing, CancellationToken token) => new MusicLyricsTimingStore(jobs).PublishAsync(jobId, variant, timing, token);
    public static Task<MusicGenerationJob> VerifyCompletedAsync(MusicGenerationJobs jobs, string jobId, int index, CancellationToken token)
        => Task.Run(() => {
            lock (MusicLyricsTimingStore.Gate(jobs.Folder(jobId))) {
                token.ThrowIfCancellationRequested(); var current = jobs.Load(jobId);
                if (index < 0 || index >= current.Variants.Length || !current.Variants[index].Completed)
                    throw new InvalidDataException("Music variant is not completed.");
                var variant = current.Variants[index];
                Match(variant.ResultPath, variant.ResultHash ?? (current.Output is null ? variant.AudioHash : null));
                if (variant.AdditionalPath is { } additional) Match(additional, variant.AdditionalHash);
                return current;
                void Match(string path, string? expected) {
                    token.ThrowIfCancellationRequested(); using var stream = File.OpenRead(path);
                    var actual = Convert.ToHexString(SHA256.HashData(stream));
                    if (expected is null || !actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Music stage checksum mismatch.");
                }
            }
        }, token);
    public async Task<MusicGenerationVariant> PublishAsync(MusicGenerationJob job, MusicGenerationVariant variant, int index,
        Action<MusicGenerationVariant> save, CancellationToken token)
    {
        var output = job.Output!;
        var metadata = MusicSongMetadata.Create(job, variant, index);
        await Publish(variant.ResultPath, output.Format, output.Bitrate, false);
        if (output.AdditionalFormat is { } extra) await Publish(variant.AdditionalPath!, extra, output.AdditionalBitrate, true);
        return variant;
        async Task Publish(string destination, MusicAudioFormat format, int bitrate, bool additional)
        {
            var knownHash = additional ? variant.AdditionalHash : variant.ResultHash;
            if (File.Exists(destination)) {
                if (knownHash is null) throw new InvalidDataException("An unrelated audio file occupies the result path.");
                await MatchHashAsync(destination, knownHash, token); return;
            }
            await MatchHashAsync(variant.AudioFile!, variant.AudioHash!, token);
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
            try {
                await encoder.EncodeAsync(variant.AudioFile!, temporary, format, bitrate, metadata, job.Cover, token);
                var hash = await HashAsync(temporary, token);
                variant = additional ? variant with { AdditionalHash = hash } : variant with { ResultHash = hash };
                save(variant); token.ThrowIfCancellationRequested(); File.Move(temporary, destination, false);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
    public static async Task<string> HashAsync(string path, CancellationToken token)
    { using var input = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(input, token)); }
    public static async Task MatchHashAsync(string path, string expected, CancellationToken token)
    { if (!string.Equals(await HashAsync(path, token), expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Music stage checksum mismatch."); }
}
