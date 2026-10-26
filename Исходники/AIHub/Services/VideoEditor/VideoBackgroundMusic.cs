using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.VideoEditor;

public sealed record VideoMusicSpec(string ModelsRoot, double TargetSeconds, string TrackName);

internal static class VideoBackgroundMusic
{
    private sealed record Receipt(string JobId, string CompanionRevision);
    internal const string Style = "instrumental, neutral background music for a tutorial, calm, unobtrusive, warm soft piano, gentle electronic textures, light steady rhythm, no vocals, no singing, no choir, no dramatic changes";
    internal static int RequestedSeconds(double target) => (int)Math.Clamp(Math.Ceiling(target), 1, 360);
    internal static async Task RunAsync(VideoProject p, VideoMusicSpec spec, string work, Action<string> status, CancellationToken token, IMusicYueWorker? worker = null)
    {
        if (!double.IsFinite(spec.TargetSeconds) || spec.TargetSeconds <= 0 || !Path.IsPathFullyQualified(spec.ModelsRoot)) throw new InvalidDataException("VideoEditor.MusicInvalid");
        var track = p.Tracks.FirstOrDefault(t => t.BackgroundMusic); if (track?.Locked == true) throw new InvalidOperationException("VideoEditor.TrackLocked");
        var jobs = new MusicGenerationJobs(Path.Combine(work, "MusicJobs")); var identity = Path.Combine(work, "music-job.json"); MusicGenerationJob job; Receipt receipt;
        if (File.Exists(identity)) { receipt = JsonSerializer.Deserialize<Receipt>(await File.ReadAllTextAsync(identity, token)) ?? throw new InvalidDataException("VideoEditor.MusicInvalid"); job = jobs.Load(receipt.JobId); }
        else {
            var expert = new MusicExpertSettings { Variation = MusicStudioRuntime.Variation };
            expert.Values["semantic_sampling.min_tokens"] = Math.Min(expert.Integer("semantic_sampling.min_tokens"), RequestedSeconds(spec.TargetSeconds) * 25);
            // The user's music workspace, lyrics, LoRAs and expert presets must not leak into this one-button tool.
            job = jobs.Create(spec.ModelsRoot, Path.Combine(work, "Music"), spec.TrackName, 1, RequestedSeconds(spec.TargetSeconds), Style, "", expert: expert,
                wishes: new MusicWishSnapshot(true, true, true, new(), []));
            receipt = new(job.Id, MusicStudioRuntime.CompanionRevision);
            var temp = identity + ".tmp"; await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(receipt), token); File.Move(temp, identity);
        }
        var runner = new MusicGenerationRunner(jobs, new VideoInstrumentalWorker(worker ?? new MusicStudioWorker())); runner.Stage += _ => status("VideoEditor.CreatingMusic");
        status("VideoEditor.CreatingMusic"); await runner.RunAsync(job.Id, token); job = jobs.Load(job.Id);
        var variant = job.Variants.Single(); var original = Path.Combine(work, "music-original.wav"); var final = Path.Combine(work, "music-background.m4a");
        var facts = new OutputProvenance { Scenario = "video-editor/background-music", CreatedUtc = job.CreatedAt, ProgramVersion = job.AppVersion,
            ModelHistory = "recorded", Models = [new(job.Variation, MusicModelVariants.Name(job.Variation), job.ModelRevision, "instrumental background music generation"),
                new(MusicModelVariants.Decoder(job.Variation), "YuE2 decoder", job.DecoderRevision, "music audio decoding"),
                new(MusicStudioRuntime.CompanionId, "YuE2 Studio decoder companion", receipt.CompanionRevision, "music decoder adapter"),
                new("runtime.music-yue2-studio", "YuE2 Studio", job.RuntimeRevision, "music inference; " + (variant.UsedRuntimePack ?? job.RuntimePack))] };
        if (!File.Exists(original)) await OutputWaveMetadata.ExportAsync(variant.ResultPath, original, facts);
        if (!File.Exists(final)) {
            status("VideoEditor.ArrangingMusic"); var duration = MusicWaveFile.ReadDuration(original).TotalSeconds; var input = original;
            var tile = Path.Combine(work, "music-loop.wav");
            if (duration < spec.TargetSeconds - .02) {
                if (!File.Exists(tile)) { var temp = tile + ".tmp"; try { await Task.Run(() => VideoMusicLoop.CreateTile(original, temp, token), token); File.Move(temp, tile); } finally { if (File.Exists(temp)) File.Delete(temp); } }
                input = tile;
            }
            var temporary = Path.Combine(work, "music-background.partial.m4a"); if (File.Exists(temporary)) File.Delete(temporary);
            try {
                var args = new List<string> { "-v", "error", "-nostdin", "-n" }; if (input == tile) args.AddRange(["-stream_loop", "-1"]);
                args.AddRange(["-i", input, "-t", VideoExport.N(spec.TargetSeconds), "-vn", "-ar", "48000", "-ac", "2", "-c:a", "aac", "-b:a", "192k", "-metadata", "LOPATA_PROVENANCE=" + facts.Json(), "-movflags", "+faststart+use_metadata_tags", "-f", "mp4", temporary]);
                await VideoRuntime.RunAsync(false, args, token); token.ThrowIfCancellationRequested(); File.Move(temporary, final);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        var source = p.Assets.FirstOrDefault(a => a.Path == original);
        if (source is null) { source = await VideoImport.ReadAsync(original, work, token); source.Models = facts.Models.ToList(); p.Assets.Add(source); }
        var asset = p.Assets.FirstOrDefault(a => a.Path == final);
        if (asset is null) { asset = await VideoImport.ReadAsync(final, work, token); asset.Models = facts.Models.ToList(); p.Assets.Add(asset); }
        if (track?.Clips.Any(c => c.GeneratedMusic && c.AssetId == asset.Id) == true) return;
        if (track is null) { track = new() { Kind = "audio", Name = spec.TrackName, BackgroundMusic = true, Volume = .15 }; p.Tracks.Add(track); }
        track.Clips.RemoveAll(c => c.GeneratedMusic);
        track.Clips.Add(new() { AssetId = asset.Id, Start = 0, Duration = Math.Min(spec.TargetSeconds, asset.Duration), FadeIn = Math.Min(.6, spec.TargetSeconds / 4), FadeOut = Math.Min(1.2, spec.TargetSeconds / 4), GeneratedMusic = true });
        VideoSeries.Ensure(p);
    }
}
