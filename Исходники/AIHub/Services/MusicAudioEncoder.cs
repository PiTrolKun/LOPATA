using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AIHub.Services;

public interface IMusicAudioEncoder
{
    Task EncodeAsync(string source, string destination, MusicAudioFormat format, int bitrate,
        IReadOnlyDictionary<string, string> metadata, CancellationToken token);
}
public sealed class MusicAudioEncoder(MusicAudioRuntime runtime) : IMusicAudioEncoder
{
    public static MusicAudioEncoder Default { get; } = new(MusicAudioRuntime.Default);
    public async Task EncodeAsync(string source, string destination, MusicAudioFormat format, int bitrate,
        IReadOnlyDictionary<string, string> metadata, CancellationToken token)
    {
        if (!MusicOutputSettings.Bitrates.Contains(bitrate)) throw new InvalidDataException("Invalid bitrate.");
        var expectedDuration = MusicWaveFile.ReadDuration(source);
        await runtime.PrepareAsync(token);
        var fileMetadata = new Dictionary<string, string>(metadata) { ["LOPATA_ENCODING"] = format +
            (MusicOutputSettings.Lossy(format) ? "; target_kbps=" + bitrate + (format == MusicAudioFormat.Opus ? "; VBR" : "; CBR") : "; lossless PCM16") };
        var tags = destination + ".metadata";
        try {
            await File.WriteAllTextAsync(tags, MusicSongMetadata.FfMetadata(fileMetadata), new UTF8Encoding(false), token);
            var args = new List<string> { "-nostdin", "-v", "error", "-n", "-i", source };
            var comments = format is MusicAudioFormat.Opus or MusicAudioFormat.Flac;
            if (comments) args.AddRange(["-f", "ffmetadata", "-i", tags]);
            args.AddRange(["-map", "0:a:0", "-map_metadata", comments ? "1" : "-1"]);
            args.AddRange(format switch {
                MusicAudioFormat.Opus => ["-c:a", "libopus", "-b:a", bitrate.ToString(CultureInfo.InvariantCulture) + "k", "-vbr", "on", "-f", "opus"],
                MusicAudioFormat.Mp3 => ["-c:a", "libmp3lame", "-b:a", bitrate.ToString(CultureInfo.InvariantCulture) + "k", "-id3v2_version", "0", "-write_id3v1", "0", "-f", "mp3"],
                MusicAudioFormat.Flac => ["-c:a", "flac", "-compression_level", "8", "-f", "flac"],
                MusicAudioFormat.Wav => ["-c:a", "pcm_s16le", "-f", "wav"],
                _ => throw new InvalidDataException("Unknown audio format.") });
            args.Add(destination); await runtime.RunAsync(false, args, token);
            await MusicId3.AttachAsync(destination, format, fileMetadata, token);
            await ValidateAsync(destination, format, expectedDuration, token, fileMetadata);
            using var file = new FileStream(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.None); file.Flush(true);
        }
        finally { if (File.Exists(tags)) File.Delete(tags); }
    }
    public async Task ValidateAsync(string path, MusicAudioFormat format, TimeSpan expected, CancellationToken token,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        using var info = JsonDocument.Parse(await runtime.RunAsync(true, ["-v", "error", "-show_entries", "stream=codec_name,sample_rate,channels:format=duration:stream_tags:format_tags",
            "-of", "json", path], token));
        var stream = info.RootElement.GetProperty("streams").EnumerateArray().Single();
        var codec = format switch { MusicAudioFormat.Opus => "opus", MusicAudioFormat.Mp3 => "mp3", MusicAudioFormat.Flac => "flac", _ => "pcm_s16le" };
        var seconds = double.Parse(info.RootElement.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        if (stream.GetProperty("codec_name").GetString() != codec || stream.GetProperty("channels").GetInt32() != 2 ||
            stream.GetProperty("sample_rate").GetString() != "48000" || Math.Abs(seconds - expected.TotalSeconds) > .1)
            throw new InvalidDataException("Encoded audio did not match the source.");
        if (metadata is not null) {
            var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Collect(stream); Collect(info.RootElement.GetProperty("format"));
            foreach (var item in metadata) {
                var key = tags.Keys.FirstOrDefault(k => k.Equals(item.Key, StringComparison.OrdinalIgnoreCase) ||
                    (item.Key is "lyrics" or "comment") && k.StartsWith(item.Key + "-", StringComparison.OrdinalIgnoreCase));
                if (key is null || tags[key] != item.Value) throw new InvalidDataException("Encoded audio lost metadata: " + item.Key);
            }
            void Collect(JsonElement element) {
                if (element.TryGetProperty("tags", out var values)) foreach (var value in values.EnumerateObject()) tags[value.Name] = value.Value.GetString()!;
            }
        }
        // Decode the whole stream, not only a readable header.
        await runtime.RunAsync(false, ["-nostdin", "-v", "error", "-xerror", "-i", path, "-map", "0:a:0", "-c:a", "pcm_s16le", "-f", "null", "-"], token);
    }
}
