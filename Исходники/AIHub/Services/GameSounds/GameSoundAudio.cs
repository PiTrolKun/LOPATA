using System.Globalization;
using System.IO;
using System.Text;
using NAudio.Wave;

namespace AIHub.Services.GameSounds;

internal static class GameSoundAudio
{
    internal static IReadOnlyList<MusicAudioFormat> Formats { get; } =
        [MusicAudioFormat.Opus, MusicAudioFormat.Mp3, MusicAudioFormat.Wav];

    private static readonly MusicAudioRuntime Runtime = MusicAudioRuntime.Default;
    private const int OpusBitrate = 64;
    private const int Mp3Bitrate = 128;

    internal static string EncodingDescription(MusicAudioFormat format) => format switch
    {
        MusicAudioFormat.Opus => $"Opus VBR {OpusBitrate} kbps",
        MusicAudioFormat.Mp3 => $"MP3 CBR {Mp3Bitrate} kbps",
        MusicAudioFormat.Wav => "WAV PCM16",
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    internal static async Task EncodeAsync(string source, string destination, MusicAudioFormat format,
        int expectedPcmLength, IReadOnlyDictionary<string, string> tags, CancellationToken token)
    {
        using var audit = ComponentAudit.CaptureOperation();
        await Runtime.PrepareAsync(token);
        var metadata = destination + ".ffmetadata";
        try
        {
            var comments = format == MusicAudioFormat.Opus;
            var args = new List<string> { "-nostdin", "-v", "error", "-n", "-i", source };
            if (comments)
            {
                await File.WriteAllTextAsync(metadata, AudioConversionMetadata.Ffmetadata(tags, []), new UTF8Encoding(false), token);
                args.AddRange(["-f", "ffmetadata", "-i", metadata]);
            }
            args.AddRange(["-map", "0:a:0", "-map_metadata", comments ? "1" : "-1", "-map_chapters", "-1"]);
            args.AddRange(format switch
            {
                MusicAudioFormat.Opus => ["-c:a", "libopus", "-b:a", OpusBitrate + "k", "-vbr", "on", "-ar", "48000", "-f", "opus"],
                MusicAudioFormat.Mp3 => ["-c:a", "libmp3lame", "-b:a", Mp3Bitrate + "k", "-id3v2_version", "0", "-write_id3v1", "0", "-f", "mp3"],
                _ => throw new ArgumentOutOfRangeException(nameof(format))
            });
            args.Add(destination);
            await Runtime.RunAsync(false, args, token);
            await AudioMetadataWriter.AttachAsync(destination, format, tags, [], token);
            var info = await AudioConversionMetadata.ReadAsync(Runtime, destination, token);
            var expectedRate = format == MusicAudioFormat.Opus ? 48000 : GameSoundSynthesizer.SampleRate;
            if (info.Codec != format.ToString().ToLowerInvariant() || info.Channels != 1 ||
                info.SampleRate != expectedRate || info.Duration <= 0 ||
                !tags.All(tag => info.Tags.TryGetValue(tag.Key, out var value) && value == tag.Value))
                throw new InvalidDataException("GameSounds.AudioValidationFailed");
            var decoded = await DecodePcmAsync(destination, format, token);
            if (Math.Abs(decoded.Length - expectedPcmLength) > GameSoundSynthesizer.Format.AverageBytesPerSecond / 100)
                throw new InvalidDataException("GameSounds.AudioValidationFailed");
        }
        finally { if (File.Exists(metadata)) File.Delete(metadata); }
    }

    internal static async Task<byte[]> ReadPcmAsync(string path, CancellationToken token)
    {
        path = Path.GetFullPath(path);
        var format = FormatFromPath(path) ?? throw new InvalidDataException("GameSounds.InvalidAudio");
        if (format == MusicAudioFormat.Wav)
            return await Task.Run(() => ReadWavePcm(path, token), token);
        using var audit = ComponentAudit.CaptureOperation();
        await Runtime.PrepareAsync(token);
        return await DecodePcmAsync(path, format, token);
    }

    internal static MusicAudioFormat? FormatFromPath(string path)
    {
        var extension = Path.GetExtension(path);
        foreach (var format in Formats)
            if (MusicOutputSettings.Extension(format).Equals(extension, StringComparison.OrdinalIgnoreCase)) return format;
        return null;
    }

    private static async Task<byte[]> DecodePcmAsync(string path, MusicAudioFormat format, CancellationToken token)
    {
        var inputFormat = format == MusicAudioFormat.Opus ? "ogg" : format.ToString().ToLowerInvariant();
        var temporary = Path.Combine(Path.GetTempPath(), $"lopata-sfx-{Guid.NewGuid():N}.wav");
        try
        {
            var maxSeconds = (GameSoundRecipes.MaxMilliseconds / 1000d + .02).ToString(CultureInfo.InvariantCulture);
            await Runtime.RunAsync(false, ["-nostdin", "-v", "error", "-xerror", "-n", "-protocol_whitelist", "file",
                "-f", inputFormat, "-i", path, "-map", "0:a:0", "-map_metadata", "-1", "-t", maxSeconds,
                "-c:a", "pcm_s16le", "-ar", GameSoundSynthesizer.SampleRate.ToString(CultureInfo.InvariantCulture),
                "-ac", "1", "-f", "wav", temporary], token);
            return ReadWavePcm(temporary, token);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static byte[] ReadWavePcm(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var reader = new WaveFileReader(path);
        var maxBytes = GameSoundSynthesizer.Format.AverageBytesPerSecond * GameSoundRecipes.MaxMilliseconds / 1000;
        if (!reader.WaveFormat.Equals(GameSoundSynthesizer.Format) || reader.Length <= 0 ||
            reader.Length > maxBytes || reader.Length % reader.WaveFormat.BlockAlign != 0)
            throw new InvalidDataException("GameSounds.InvalidAudio");
        var pcm = new byte[reader.Length];
        reader.ReadExactly(pcm);
        token.ThrowIfCancellationRequested();
        return pcm;
    }
}
