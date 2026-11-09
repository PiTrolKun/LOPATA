using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.VideoUpscale;

internal static class UpscaleAssembly
{
    internal const string PartialKind = "video-upscale-partial";
    internal static UpscaleJob Available(UpscaleJob source, string output)
    {
        UpscaleCatalog.Validate(source.Project);
        if (File.Exists(output) || Path.GetFullPath(output).Equals(Path.GetFullPath(source.Project.Source), StringComparison.OrdinalIgnoreCase)) throw new IOException("VideoUpscale.OutputExists");
        var job = UpscaleStore.Clone(source); job.Id = Guid.NewGuid().ToString("N"); job.Output = Path.GetFullPath(output); job.Complete = false; job.FinalArtifact = null;
        job.Parts.Clear(); job.Frames = 0;
        for (var i = 0; source.Parts.TryGetValue(i + "/ready", out var part); i++) {
            var expected = Path.Combine(Path.GetDirectoryName(UpscaleStore.JobPath(source))!, $"{i:D6}-ready.mkv");
            if (part.Path != expected) throw new InvalidDataException("VideoUpscale.CheckpointDamaged");
            job.Parts.Add(i + "/ready", part); job.Frames += part.Frames;
        }
        if (job.Frames < 1) throw new InvalidOperationException("VideoUpscale.NoReadyParts");
        UpscaleStore.Save(UpscaleStore.JobPath(job), job); return job;
    }
    internal static Task<string> StartPartial(UpscaleJob job, IProgress<UpscaleProgress> progress, CancellationToken token, string title, BackgroundOperationState? restored = null) =>
        ApplicationBackgroundOperations.RunAsync(PartialKind, title, UpscaleStore.ProjectPath(job.Project.Id), new UpscaleCheckpoint(UpscaleStore.JobPath(job)),
            ct => Partial(UpscaleStore.Load<UpscaleJob>(UpscaleStore.JobPath(job)), progress, ct), token, restored);
    private static async Task<string> Partial(UpscaleJob job, IProgress<UpscaleProgress> progress, CancellationToken token)
    {
        UpscaleCatalog.Validate(job.Project); await UpscaleFfmpeg.PrepareAsync(token);
        if (job.FinalArtifact is {} final && final.Path == job.Output && await UpscaleMediaTools.Valid(final, token)) return job.Output;
        using var source = new FileStream(job.Project.Source, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (source.Length != job.Project.SourceLength || await UpscaleMediaTools.Hash(job.Project.Source, token) != job.Project.SourceSha256) throw new IOException("VideoUpscale.SourceChanged");
        var paths = new List<string>(); long frames = 0;
        for (var i = 0; i < job.Parts.Count; i++) {
            if (!job.Parts.TryGetValue(i + "/ready", out var part) || !Path.GetFullPath(part.Path).StartsWith(Path.GetFullPath(UpscaleStore.Work(job.Project.Id)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("VideoUpscale.CheckpointDamaged");
            Lopata.Updates.SafeUpdatePath.RejectLinks(part.Path);
            if (!await UpscaleMediaTools.Valid(part, token)) throw new InvalidDataException("VideoUpscale.CheckpointDamaged");
            paths.Add(part.Path); frames += part.Frames;
        }
        if (frames != job.Frames || frames < 1) throw new InvalidDataException("VideoUpscale.CheckpointDamaged");
        return await Publish(job, paths, true, progress, token);
    }
    internal static async Task<string> Publish(UpscaleJob job, List<string> results, bool partial, IProgress<UpscaleProgress> progress, CancellationToken token)
    {
        if (File.Exists(job.Output)) throw new IOException("VideoUpscale.OutputExists");
        progress.Report(new(.98, "VideoUpscale.Encoding"));
        var project = job.Project; var jobFile = UpscaleStore.JobPath(job); var concat = Path.Combine(Path.GetDirectoryName(jobFile)!, "concat.txt");
        await File.WriteAllLinesAsync(concat, results.SelectMany((p, i) => new[] { "file '" + p.Replace('\\', '/').Replace("'", "'\\''") + "'", "duration " + UpscaleMediaTools.N(Math.Min(job.ChunkFrames, job.Frames - (long)i * job.ChunkFrames) / project.Media.Fps) }), token);
        var metadata = new OutputProvenance { Scenario = partial ? $"video-upscale/partial/frames-{job.StartFrame}-{job.StartFrame + job.Frames - 1}" : "video-upscale/" + (job.Preview ? "preview" : "export"), ModelHistory = "recorded",
            Models = [new OutputModelUse("video-upscale-ffmpeg", "FFmpeg LGPL", UpscaleFfmpeg.Revision, "video processing"),
                .. job.Components.Select(c => JsonSerializer.Deserialize<OutputModelUse>(JsonSerializer.Serialize(c))!)] };
        Directory.CreateDirectory(Path.GetDirectoryName(job.Output)!);
        var staging = job.Output + "." + job.Id + ".partial" + Path.GetExtension(job.Output);
        // Same encoder settings in every part; keep AVC unchanged for MP4/Matroska.
        List<string> args = ["-v", "error", "-y", "-f", "concat", "-safe", "0", "-auto_convert", "0", "-i", concat, "-ss", UpscaleMediaTools.N(job.StartFrame / project.Media.Fps), "-i", project.Source,
            "-map", "0:v:0", "-map", "1:a?", "-map_metadata", "-1", "-t", UpscaleMediaTools.N(job.Frames / project.Media.Fps), "-c:v", "copy",
            "-c:a", Path.GetExtension(job.Output).Equals(".mkv", StringComparison.OrdinalIgnoreCase) ? "copy" : "aac", "-metadata", "comment=" + metadata.Json()];
        if (Path.GetExtension(job.Output).Equals(".mp4", StringComparison.OrdinalIgnoreCase)) args.AddRange(["-movflags", "+faststart+use_metadata_tags"]);
        args.Add(staging); await UpscaleFfmpeg.RunAsync(false, args, token);
        var last = project.Steps.Last(s => s.Enabled);
        job.FinalArtifact = (await UpscaleMediaTools.Check(staging, job.Frames, last.Width, last.Height, token)) with { Path = job.Output }; UpscaleStore.Save(jobFile, job);
        token.ThrowIfCancellationRequested(); Lopata.Updates.SafeUpdatePath.RejectLinks(job.Output); File.Move(staging, job.Output);
        CollectionExportJournal.Record(job.Output, metadata.Scenario);
        job.Complete = true; UpscaleStore.Save(jobFile, job); progress.Report(new(1, "VideoUpscale.Complete")); return job.Output;
    }
}
