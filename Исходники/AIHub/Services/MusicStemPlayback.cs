using System.IO;
using AIHub.Models;

namespace AIHub.Services;

public enum MusicStemPlaybackMode { Together, Voice, Music }

public static class MusicStemPlayback
{
    public static async Task<string> ForClipAsync(MusicTrack track, MusicGenerationJobs jobs, CancellationToken token)
    {
        if (track.JobId is null) throw new InvalidDataException("Unlinked layered song.");
        var job = await MusicOutputPublisher.VerifyCompletedAsync(jobs, track.JobId, track.Variant, token);
        var variant = job.Variants[track.Variant];
        if (variant.Stems?.Published != true) throw new InvalidDataException("Incomplete song bundle.");
        var path = await MixAsync(track, token, jobs);
        try {
            await AudioMetadataWriter.AttachAsync(path, MusicAudioFormat.Wav, MusicStemPublisher.Metadata(job, variant, track.Variant, "together-playback"),
                [job.Cover is null ? MusicCoverArt.Default : MusicCoverStore.ReadArtwork(job.Cover)], token);
            var timing = await new MusicLyricsTimingStore(jobs).ReadAsync(job, track.Variant, token);
            if (timing is not null) {
                // MixAsync sums equal-length decoded buffers sample-for-sample; the voice clock is retained.
                if (track.Path == variant.ResultPath && Math.Abs(MusicWaveFile.ReadDuration(path).TotalSeconds - timing.DurationSeconds) <= .001) {
                    var mapped = timing with { AudioPayloadHash = await MusicLyricsTimingMetadata.AudioPayloadHashAsync(path, token), Clock = null,
                        Derivations = [.. timing.Derivations, new(timing.AudioPayloadHash, "sum-aligned-decoded-stems", 48000,
                            (long)Math.Round(MusicWaveFile.ReadDuration(path).TotalSeconds * 48000))] };
                    mapped.Validate();
                    var staged = path + ".timing.tmp";
                    try { await MusicLyricsTimingMetadata.WriteCopyAsync(path, staged, mapped, token); File.Move(staged, path, true); }
                    finally { if (File.Exists(staged)) File.Delete(staged); }
                }
            }
            return path;
        }
        catch { if (File.Exists(path)) File.Delete(path); throw; }
    }
    public static async Task VerifyAsync(MusicTrack track, CancellationToken token, MusicGenerationJobs? jobs = null)
    {
        if (!track.IsLayered || track.Path is null || track.MusicPath is null) throw new InvalidDataException("Missing song layers.");
        if (track.JobId is null) return;
        var job = await MusicOutputPublisher.VerifyCompletedAsync(jobs ?? MusicGenerationJobs.Default, track.JobId, track.Variant, token);
        var v = job.Variants[track.Variant]; var additional = track.Path == v.AdditionalPath;
        if (v.Stems?.Published != true || track.Path != v.ResultPath && !additional ||
            track.MusicPath != MusicStemBundle.MusicPath(v, additional) || track.BundleFolder != Path.GetDirectoryName(v.ResultPath))
            throw new InvalidDataException("Playback layers do not belong to this song bundle.");
    }
    public static async Task<string> MixAsync(MusicTrack track, CancellationToken token, MusicGenerationJobs? jobs = null)
    {
        if (!track.IsLayered || track.Path is null || track.MusicPath is null) throw new InvalidDataException("Missing song layers.");
        await VerifyAsync(track, token, jobs);
        var runtime = MusicAudioRuntime.Default; await runtime.PrepareAsync(token);
        var directory = Path.Combine(AppDataPaths.BaseDirectory, "Music", "Playback"); Directory.CreateDirectory(directory);
        var mixed = Path.Combine(directory, "stems-" + Guid.NewGuid().ToString("N") + ".wav");
        var voice = mixed + ".voice.wav"; var music = mixed + ".music.wav";
        try {
            await runtime.DecodeAsync(track.Path, voice, token); await runtime.DecodeAsync(track.MusicPath, music, token);
            await Task.Run(() => MusicPcm16.Mix(voice, music, mixed, token), token);
            if (Math.Abs(MusicWaveFile.ReadDuration(mixed).TotalSeconds - track.Duration.TotalSeconds) > .12) throw new InvalidDataException("Layer playback clocks differ.");
            return mixed;
        }
        catch { if (File.Exists(mixed)) File.Delete(mixed); throw; }
        finally { if (File.Exists(voice)) File.Delete(voice); if (File.Exists(music)) File.Delete(music); }
    }
}
