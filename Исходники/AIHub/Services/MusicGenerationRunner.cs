using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;
using AIHub.Models;

namespace AIHub.Services;

public sealed class MusicGenerationRunner(MusicGenerationJobs jobs, IMusicYueWorker worker, IMusicAudioEncoder? encoder = null,
    Func<MusicGenerationJob, int, CancellationToken, Task>? synchronizeLyrics = null,
    IMusicStemsWorker? stemsWorker = null, Action<string>? log = null, IMusicCoverScoreWorker? coverScoreWorker = null, IMusicLayerFitter? layerFitter = null)
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
                if (variant.KaraokeReadiness != MusicKaraokeReadiness.Pending) {
                    variant = variant with { KaraokeReadiness = MusicKaraokeReadiness.Pending }; Save(variant);
                }
                var elapsed = Stopwatch.StartNew();
                var previousSeconds = variant.GenerationSeconds ?? 0;
                try {
                var request = new MusicYueRequest(job.Style, job.Lyrics, variant.LanguageSeed, variant.SoundSeed, job.DurationSeconds)
                    { Expert = job.Expert.Snapshot(), Wishes = job.Wishes?.Snapshot(), DurationAutomatic = job.DurationAutomatic, Loras = job.Loras.ToArray(),
                        StudioRuntimeRevision = job.Variation == MusicStudioRuntime.Variation ? job.RuntimeRevision : null,
                        StudioRuntimePack = job.Variation == MusicStudioRuntime.Variation ? job.RuntimePack : null };
                var transform = new MusicAudioTaskPipeline(coverScoreWorker, stemsWorker, log, layerFitter);
                if (job.Melody is { Enabled: true }) {
                    request = await MusicMidi.MusicMelodyPipeline.PrepareAsync(jobs, job, variant, request, Save, token);
                    variant = jobs.Load(id).Variants[i];
                }
                if (job.AudioTask is { Enabled: true }) {
                    request = await transform.PrepareAsync(job, variant, request, Save, token);
                    variant = jobs.Load(id).Variants[i];
                }
                var restoration = job.AudioTask is { Enabled: true, Mode: MusicAudioTaskMode.Restore };
                if (!restoration && job.Expert.Cot != "off" && variant.PlanHash is null)
                {
                    var plan = jobs.StagePath(id, ".abc"); Stage?.Invoke(MusicGenerationStage.Loading);
                    await worker.PlanAsync(Artifact(job.Variation), request, jobs.StagePath(id, ".json"), plan, token);
                    variant = variant with { PlanFile = plan, PlanHash = await HashAsync(plan, token),
                        GenerationSeconds = previousSeconds + elapsed.Elapsed.TotalSeconds, PlanHardware = worker.LastHardware }; Save(variant);
                }
                if (!restoration && job.Expert.Cot != "off") await MatchHashAsync(variant.PlanFile!, variant.PlanHash!, token);
                if (variant.AudioHash is null)
                {
                    if (restoration) variant = await transform.FinishAsync(job, variant, Save, token);
                    else if (job.AudioTask is { Enabled: true } && variant.AudioTask?.RenderHash is not null)
                        variant = await transform.FinishAsync(job, variant, Save, token);
                    else {
                    var audio = jobs.StagePath(id, ".wav"); Stage?.Invoke(MusicGenerationStage.Loading);
                    if (job.Expert.Cot != "off") request = request with { Abc = await File.ReadAllTextAsync(variant.PlanFile!, token) };
                    await worker.SynthesizeAsync(Artifact(job.Variation), Artifact(MusicModelVariants.Decoder(job.Variation)), request,
                        jobs.StagePath(id, ".json"), audio, token);
                    _ = MusicWaveFile.ReadDuration(audio);
                    if (job.AudioTask is { Enabled: true }) {
                        variant = variant with { AudioTask = new() { RenderPath = audio, RenderHash = await HashAsync(audio, token) },
                            Hardware = worker.LastHardware, UsedRuntimePack = worker.LastRuntimePack, ExecutionReceipt = worker.LastRequestReceipt };
                        Save(variant); variant = await transform.FinishAsync(job, variant, Save, token);
                    } else {
                    variant = variant with { AudioFile = audio, AudioHash = await HashAsync(audio, token),
                        DurationSeconds = MusicWaveFile.ReadDuration(audio).TotalSeconds, GenerationSeconds = previousSeconds + elapsed.Elapsed.TotalSeconds,
                        Hardware = worker.LastHardware, UsedRuntimePack = worker.LastRuntimePack,
                        ExecutionReceipt = worker.LastRequestReceipt }; Save(variant);
                    }
                    }
                }
                }
                finally {
                    variant = jobs.Load(id).Variants[i] with { GenerationSeconds = previousSeconds + elapsed.Elapsed.TotalSeconds }; Save(variant);
                }
                // AudioHash is durable before publishing. Recovery can recognise the completed move.
                Stage?.Invoke(MusicGenerationStage.Encoding);
                if (job.SeparateTracks)
                    variant = await new MusicStemPublisher(stemsWorker ?? new MusicStemsWorker(), encoder ?? MusicAudioEncoder.Default, log)
                        .PublishAudioAsync(job, variant, i, Save, token);
                else if (job.Output is not null)
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
                if (!job.SeparateTracks && job.Output is not null && variant.AudioFile is { } nativeAudio && File.Exists(nativeAudio)) File.Delete(nativeAudio);
            }
            job = await MusicOutputPublisher.VerifyCompletedAsync(jobs, id, i, token);
            variant = job.Variants[i];
            void Save(MusicGenerationVariant updated)
            {
                MusicGenerationVariant[] variants;
                lock (MusicLyricsTimingStore.Gate(jobs.Folder(id))) {
                    var latest = jobs.Load(id); variants = latest.Variants.ToArray(); variants[i] = updated;
                    job = latest with { Variants = variants }; jobs.Save(job);
                }
                if (ApplicationBackgroundOperations.Current is { IsInOperationScope: true } controller)
                    controller.SaveCheckpoint(new { JobId = id, Completed = variants.Count(v => v.Completed) });
            }
        }
        // Audio receipts are durable. Expose each new result only after its karaoke preparation settles.
        for (var i = 0; i < job.Variants.Length; i++) {
            token.ThrowIfCancellationRequested(); job = jobs.Load(id);
            if (job.Variants[i].KaraokeReadiness is null or MusicKaraokeReadiness.Pending) {
                Stage?.Invoke(MusicGenerationStage.Synchronizing);
                try {
                    if (job.Wishes?.Instrumental == true || string.IsNullOrWhiteSpace(job.Lyrics))
                        job = jobs.SetKaraokeReadiness(id, i, MusicKaraokeReadiness.Ready);
                    else if (synchronizeLyrics is not null) await synchronizeLyrics(job, i, token);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) {
                    OwnedProcessRegistry.Log("music_karaoke_preparation_failed", "Music.LyricsAlignment", detail: error.GetType().Name);
                    job = jobs.SetKaraokeReadiness(id, i, MusicKaraokeReadiness.Unavailable);
                }
                job = jobs.Load(id);
                if (job.Variants[i].KaraokeReadiness == MusicKaraokeReadiness.Pending)
                    job = jobs.SetKaraokeReadiness(id, i, MusicKaraokeReadiness.Unavailable);
            }
            token.ThrowIfCancellationRequested();
            if (job.SeparateTracks) {
                try { await MusicStemPublisher.FinalizeAsync(jobs, id, i, token); }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) { throw new MusicStemsException(error); }
            }
            job = await MusicOutputPublisher.VerifyCompletedAsync(jobs, id, i, token);
            if (MusicGenerationJobs.ResultVisible(job.Variants[i])) TrackReady?.Invoke(MusicGenerationJobs.Track(job, job.Variants[i], i));
            if (job.SeparateTracks && job.Variants[i].Stems?.Published == true) MusicStemCleanup.Run(jobs, job, i, log);
            if (ApplicationBackgroundOperations.Current is { IsInOperationScope: true } controller)
                controller.SaveCheckpoint(new { JobId = id, Completed = job.Variants.Count(v => v.Completed),
                    KaraokeSettled = job.Variants.Count(MusicGenerationJobs.ResultVisible) });
        }
    }
    private static async Task<string> HashAsync(string path, CancellationToken token)
    { using var input = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(input, token)); }
    private static async Task MatchHashAsync(string path, string expected, CancellationToken token)
    { if (!string.Equals(await HashAsync(path, token), expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Music stage checksum mismatch."); }
}
