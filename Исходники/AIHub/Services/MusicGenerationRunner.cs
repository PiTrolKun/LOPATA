using System.IO;
using System.Security.Cryptography;
using AIHub.Models;

namespace AIHub.Services;

public sealed class MusicGenerationRunner(MusicGenerationJobs jobs, IMusicYueWorker worker)
{
    public const string BackgroundKind = "music.generate";
    public event Action<MusicGenerationStage>? Stage;
    public event Action<MusicTrack>? TrackReady;
    public async Task RunAsync(string id, CancellationToken token)
    {
        var job = jobs.Load(id);
        var cards = MusicComponentCatalog.CreateCards(job.ModelsRoot);
        string Artifact(string key) { var card = cards.Single(c => c.ModelArtifactId == key); return Path.Combine(card.InstallDirectory, card.Files.Single().RelativePath); }
        for (var i = 0; i < job.Variants.Length; i++)
        {
            token.ThrowIfCancellationRequested(); var variant = job.Variants[i];
            if (!variant.Completed)
            {
                var request = new MusicYueRequest(job.Style, job.Lyrics, variant.LanguageSeed, variant.SoundSeed, job.DurationSeconds);
                if (variant.PlanHash is null)
                {
                    var plan = jobs.StagePath(id, ".abc"); Stage?.Invoke(MusicGenerationStage.Loading);
                    await worker.PlanAsync(Artifact(MusicComponentCatalog.ModelId), request, jobs.StagePath(id, ".json"), plan, token);
                    variant = variant with { PlanFile = plan, PlanHash = await HashAsync(plan, token) }; Save(variant);
                }
                await MatchHashAsync(variant.PlanFile!, variant.PlanHash!, token);
                if (variant.AudioHash is null)
                {
                    var audio = jobs.StagePath(id, ".wav"); Stage?.Invoke(MusicGenerationStage.Loading);
                    request = request with { Abc = await File.ReadAllTextAsync(variant.PlanFile!, token) };
                    await worker.SynthesizeAsync(Artifact(MusicComponentCatalog.ModelId), Artifact(MusicComponentCatalog.DecoderId), request,
                        jobs.StagePath(id, ".json"), audio, token);
                    _ = MusicWaveFile.ReadDuration(audio);
                    variant = variant with { AudioFile = audio, AudioHash = await HashAsync(audio, token) }; Save(variant);
                }
                // AudioHash is durable before publishing. Recovery can recognise the completed move.
                Stage?.Invoke(MusicGenerationStage.Encoding);
                if (File.Exists(variant.ResultPath)) await MatchHashAsync(variant.ResultPath, variant.AudioHash!, token);
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
            }
            await MatchHashAsync(variant.ResultPath, variant.AudioHash!, token);
            TrackReady?.Invoke(new(variant.ResultPath, Path.GetFileName(variant.ResultPath), MusicWaveFile.ReadDuration(variant.ResultPath), job.CreatedAt)
                { JobId = id, Variant = i });
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
