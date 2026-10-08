using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;
using AIHub.Models;

namespace AIHub.Services;

public sealed class MusicGenerationRunner(MusicGenerationJobs jobs, IMusicYueWorker worker, IMusicAudioEncoder? encoder = null)
{
    public const string BackgroundKind = "music.generate";
    public event Action<MusicGenerationStage>? Stage;
    public event Action<MusicTrack>? TrackReady;
    public async Task RunAsync(string id, CancellationToken token)
    {
        var job = jobs.Load(id);
        string Artifact(string key) => MusicModelVariants.Artifact(job.ModelsRoot, job.Variation, key);
        for (var i = 0; i < job.Variants.Length; i++)
        {
            token.ThrowIfCancellationRequested(); var variant = job.Variants[i];
            if (!variant.Completed)
            {
                var elapsed = Stopwatch.StartNew();
                var previousSeconds = variant.GenerationSeconds ?? 0;
                try {
                var request = new MusicYueRequest(job.Style, job.Lyrics, variant.LanguageSeed, variant.SoundSeed, job.DurationSeconds)
                    { Expert = job.Expert.Snapshot() };
                if (job.Expert.Cot != "off" && variant.PlanHash is null)
                {
                    var plan = jobs.StagePath(id, ".abc"); Stage?.Invoke(MusicGenerationStage.Loading);
                    await worker.PlanAsync(Artifact(job.Variation), request, jobs.StagePath(id, ".json"), plan, token);
                    variant = variant with { PlanFile = plan, PlanHash = await HashAsync(plan, token),
                        GenerationSeconds = previousSeconds + elapsed.Elapsed.TotalSeconds, PlanHardware = worker.LastHardware }; Save(variant);
                }
                if (job.Expert.Cot != "off") await MatchHashAsync(variant.PlanFile!, variant.PlanHash!, token);
                if (variant.AudioHash is null)
                {
                    var audio = jobs.StagePath(id, ".wav"); Stage?.Invoke(MusicGenerationStage.Loading);
                    if (job.Expert.Cot != "off") request = request with { Abc = await File.ReadAllTextAsync(variant.PlanFile!, token) };
                    await worker.SynthesizeAsync(Artifact(job.Variation), Artifact(MusicModelVariants.Decoder(job.Variation)), request,
                        jobs.StagePath(id, ".json"), audio, token);
                    _ = MusicWaveFile.ReadDuration(audio);
                    variant = variant with { AudioFile = audio, AudioHash = await HashAsync(audio, token),
                        DurationSeconds = MusicWaveFile.ReadDuration(audio).TotalSeconds, GenerationSeconds = previousSeconds + elapsed.Elapsed.TotalSeconds,
                        Hardware = worker.LastHardware, UsedRuntimePack = worker.LastRuntimePack,
                        ExecutionReceipt = worker.LastRequestReceipt }; Save(variant);
                }
                }
                finally {
                    if (variant.AudioHash is null) { variant = variant with { GenerationSeconds = previousSeconds + elapsed.Elapsed.TotalSeconds }; Save(variant); }
                }
                // AudioHash is durable before publishing. Recovery can recognise the completed move.
                Stage?.Invoke(MusicGenerationStage.Encoding);
                if (job.Output is not null)
                    variant = await new MusicOutputPublisher(encoder ?? MusicAudioEncoder.Default).PublishAsync(job, variant, i, Save, token);
                else if (File.Exists(variant.ResultPath)) await MatchHashAsync(variant.ResultPath, variant.AudioHash!, token);
                else
                {
                    await MatchHashAsync(variant.AudioFile!, variant.AudioHash!, token);
                    var temporary = variant.ResultPath + "." + Guid.NewGuid().ToString("N") + ".partial";
                    try
                    {
                        using (var source = File.OpenRead(variant.AudioFile!))
                        using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        { await source.CopyToAsync(target, token); target.Flush(flushToDisk: true); }
                        await MatchHashAsync(temporary, variant.AudioHash!, token); _ = MusicWaveFile.ReadDuration(temporary);
                        File.Move(temporary, variant.ResultPath, false);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
                variant = variant with { Completed = true }; Save(variant);
                if (job.Output is not null && variant.AudioFile is { } nativeAudio && File.Exists(nativeAudio)) File.Delete(nativeAudio);
            }
            await MatchHashAsync(variant.ResultPath, variant.ResultHash ?? variant.AudioHash!, token);
            if (variant.AdditionalPath is { } additional) await MatchHashAsync(additional, variant.AdditionalHash!, token);
            TrackReady?.Invoke(MusicGenerationJobs.Track(job, variant, i));
            void Save(MusicGenerationVariant updated)
            {
                var variants = job.Variants.ToArray(); variants[i] = updated; job = job with { Variants = variants }; jobs.Save(job);
                if (ApplicationBackgroundOperations.Current is { IsInOperationScope: true } controller)
                    controller.SaveCheckpoint(new { JobId = id, Completed = variants.Count(v => v.Completed) });
            }
        }
    }
    private static async Task<string> HashAsync(string path, CancellationToken token)
    { using var input = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(input, token)); }
    private static async Task MatchHashAsync(string path, string expected, CancellationToken token)
    { if (!string.Equals(await HashAsync(path, token), expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Music stage checksum mismatch."); }
}
