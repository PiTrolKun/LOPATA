using System.IO;
using System.Security.Cryptography;
using AIHub.Models;

namespace AIHub.Services.MusicMidi;

public static class MusicMelodyPipeline
{
    public static bool Supported(string variation) => variation is MusicStudioRuntime.Variation or MusicAceCatalog.Variation;
    public static void Validate(MusicMelody? melody, MusicAudioTask? audio, string variation) {
        melody?.Validate(); if (melody?.Enabled != true) return;
        if (!Supported(variation)) throw new InvalidDataException("Music.Midi.Unsupported");
        if (audio?.Enabled == true) throw new InvalidDataException("Music.Midi.Conflict");
    }
    public static MusicGenerationJob Capture(MusicGenerationJob job, MusicMelody melody) {
        Validate(melody, job.AudioTask, job.Variation);
        if (!melody.Enabled) return job with { Melody = melody.Snapshot() };
        var expert = job.Expert.Snapshot();
        if (job.Variation == MusicStudioRuntime.Variation) expert.Values["cot"] = 1;
        return job with { Melody = melody.Snapshot(), Expert = expert, DurationAutomatic = false,
            DurationSeconds = Math.Max(1, (int)Math.Ceiling(melody.Seconds)) };
    }
    public static async Task<MusicYueRequest> PrepareAsync(MusicGenerationJobs jobs, MusicGenerationJob job, MusicGenerationVariant variant,
        MusicYueRequest request, Action<MusicGenerationVariant> save, CancellationToken token) {
        Validate(job.Melody, job.AudioTask, job.Variation);
        if (job.Melody is not { Enabled: true } melody) return request;
        if (job.Variation == MusicStudioRuntime.Variation) {
            if (variant.PlanHash is null) {
                var path = jobs.StagePath(job.Id, ".abc"); await File.WriteAllTextAsync(path, MusicMelodyAbc.Build(melody), token);
                save(variant with { PlanFile = path, PlanHash = await Hash(path, token), PlanHardware = "user MIDI melody" });
            }
            return request;
        }
        var audio = variant.MelodyInputPath;
        if (audio is null) {
            audio = jobs.StagePath(job.Id, ".wav");
            try { await Task.Run(() => MusicMelodyWave.Write(audio, melody, token), token); }
            catch { if (File.Exists(audio)) File.Delete(audio); throw; }
            variant = variant with { MelodyInputPath = audio, MelodyInputHash = await Hash(audio, token) }; save(variant);
        }
        if (!string.Equals(await Hash(audio, token), variant.MelodyInputHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Music.Midi.ChangedInput");
        return request with { AudioTask = new() { Enabled = true, Mode = MusicAudioTaskMode.Cover,
            Source = new(audio) { Name = "MIDI melody.wav", Sha256 = variant.MelodyInputHash }, SourceStrength = .65 } };
    }
    private static async Task<string> Hash(string path, CancellationToken token) {
        using var input = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(input, token));
    }
}
