using System.IO;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Proves the shared sample timeline; never correlates a mix against a different separated signal.</summary>
internal static class MusicLyricsStemClock
{
    internal static async Task<MusicTimedLyrics?> MapAsync(MusicGenerationJob job, MusicGenerationVariant variant,
        MusicTimedLyrics measured, string temporaryDirectory, CancellationToken token)
    {
        var receipt = variant.Stems?.Receipt ?? throw new InvalidDataException("Missing separated clock receipt.");
        if (receipt.SourceHash != variant.AudioHash || variant.AudioFile is null) throw new InvalidDataException("Unlinked separated source clock.");
        await MusicOutputPublisher.MatchHashAsync(variant.AudioFile, receipt.SourceHash, token);
        await MusicOutputPublisher.MatchHashAsync(receipt.VoicePcm, receipt.VoiceHash, token);
        await MusicOutputPublisher.MatchHashAsync(receipt.MusicPcm, receipt.MusicHash, token);
        var sourcePayload = await MusicLyricsTimingMetadata.AudioPayloadHashAsync(variant.AudioFile, token);
        if (measured.AudioPayloadHash != sourcePayload) throw new InvalidDataException("Measured source clock changed.");
        var original = MusicWaveFile.ReadDuration(variant.AudioFile);
        var duration = MusicWaveFile.ReadDuration(receipt.VoicePcm);
        if (Math.Abs(original.TotalSeconds - duration.TotalSeconds) > 1d / 48000 ||
            MusicWaveFile.ReadDuration(receipt.MusicPcm) != duration) throw new InvalidDataException("Separated sample timelines differ.");
        var nativeVoice = measured with { AudioPayloadHash = await MusicLyricsTimingMetadata.AudioPayloadHashAsync(receipt.VoicePcm, token),
            DurationSeconds = duration.TotalSeconds, Clock = null,
            Derivations = [.. measured.Derivations, new(sourcePayload, "htdemucs-aligned-pcm", 48000, (long)Math.Round(duration.TotalSeconds * 48000))] };
        nativeVoice.Validate();
        // Same signal now: determine any encoder delay using the existing verified codec-clock mapper.
        return await new MusicLyricsTimingClock(MusicAudioRuntime.Default).MapAsync(receipt.VoicePcm, variant.ResultPath, nativeVoice, temporaryDirectory, token);
    }
}
