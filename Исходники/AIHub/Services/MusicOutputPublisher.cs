using System.IO;
using System.Security.Cryptography;

namespace AIHub.Services;

/// <summary>Every encoding starts from native PCM. Hashes are checkpointed before publishing either file.</summary>
public sealed class MusicOutputPublisher(IMusicAudioEncoder encoder)
{
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
                await encoder.EncodeAsync(variant.AudioFile!, temporary, format, bitrate, metadata, token);
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
