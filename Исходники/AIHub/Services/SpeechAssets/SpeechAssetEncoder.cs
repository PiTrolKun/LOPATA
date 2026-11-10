using System.IO;
using System.Globalization;

namespace AIHub.Services.SpeechAssets;

/// <summary>Speech-sized output through the existing audited/licensed audio runtime.</summary>
internal sealed class SpeechAssetEncoder(MusicAudioRuntime? runtime = null)
{
    internal static int[] Bitrates => [32, 48, 64, 96, 128, 160, 192, 256, 320];
    private readonly MusicAudioRuntime _runtime = runtime ?? MusicAudioRuntime.Default;
    private bool _prepared;
    internal async Task EncodeAsync(string source, string destination, MusicAudioFormat format, int bitrate,
        SpeechAssetTake take, SpeechAssetRow row, string? clipId, CancellationToken token)
    {
        if (!Enum.IsDefined(format) || !Bitrates.Contains(bitrate)) throw new InvalidDataException("SpeechAssets.Invalid");
        if (!_prepared) { await _runtime.PrepareAsync(token); _prepared = true; }
        var facts = new OutputProvenance { Scenario = "speech-assets/export", CreatedUtc = take.CreatedUtc,
            ModelHistory = "recorded", Models = take.Models };
        var marker = System.Text.Json.JsonSerializer.Serialize(new { Schema = 1, ClipId = clipId, RowId = row.Id,
            TakeId = take.Id, Language = take.Voice.Language, row.SourceText, SynthesisText = take.Text,
            take.Voice, take.Models, take.CreatedUtc });
        var tags = new Dictionary<string, string> { ["encoded_by"] = "LOPATA", ["LOPATA_PROVENANCE"] = facts.Json(),
            ["LOPATA_SPEECH_ASSET"] = marker };
        var metadata = destination + ".ffmetadata";
        try
        {
            await File.WriteAllTextAsync(metadata, AudioConversionMetadata.Ffmetadata(tags, []), token);
            var comments = format is MusicAudioFormat.Opus or MusicAudioFormat.Flac;
            var args = new List<string> { "-nostdin", "-v", "error", "-n", "-i", source };
            if (comments) args.AddRange(["-f", "ffmetadata", "-i", metadata]);
            args.AddRange(["-map", "0:a:0", "-map_metadata", comments ? "1" : "-1", "-map_chapters", "-1"]);
            var rate = bitrate.ToString(CultureInfo.InvariantCulture) + "k";
            args.AddRange(format switch
            {
                MusicAudioFormat.Opus => ["-c:a", "libopus", "-b:a", rate, "-vbr", "on", "-ar", "48000", "-f", "opus"],
                MusicAudioFormat.Mp3 => ["-c:a", "libmp3lame", "-b:a", rate, "-id3v2_version", "0", "-write_id3v1", "0", "-f", "mp3"],
                MusicAudioFormat.Flac => ["-c:a", "flac", "-compression_level", "5", "-f", "flac"],
                _ => ["-c:a", "pcm_s16le", "-f", "wav"]
            });
            args.Add(destination);
            await _runtime.RunAsync(false, args, token);
            await AudioMetadataWriter.AttachAsync(destination, format, tags, [], token);
            var info = await AudioConversionMetadata.ReadAsync(_runtime, destination, token);
            using var original = SpeechAssetWork.OpenPcm(source);
            var codec = format switch { MusicAudioFormat.Opus => "opus", MusicAudioFormat.Mp3 => "mp3", MusicAudioFormat.Flac => "flac", _ => "pcm_s16le" };
            if (info.Codec != codec || info.Channels != original.WaveFormat.Channels || info.Duration <= 0
                || Math.Abs(info.Duration - original.TotalTime.TotalSeconds) > .2
                || !tags.All(t => info.Tags.TryGetValue(t.Key, out var value) && value == t.Value))
                throw new InvalidDataException("AudioShell.ValidationFailed");
            await _runtime.RunAsync(false, ["-nostdin", "-v", "error", "-xerror", "-i", destination, "-map", "0:a:0", "-f", "null", "-"], token);
        }
        finally { if (File.Exists(metadata)) File.Delete(metadata); }
    }
}
