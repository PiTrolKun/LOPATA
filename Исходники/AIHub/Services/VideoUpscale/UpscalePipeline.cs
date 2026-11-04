using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services.VideoEditor;

namespace AIHub.Services.VideoUpscale;

internal sealed class UpscalePipeline
{
    internal const string Kind = "video-upscale";
    internal static UpscaleJob Create(UpscaleProject project, bool preview, string output)
    {
        UpscaleCatalog.Validate(project);
        project.Workspace = UpscaleStore.Work(project.Id);
        if (Path.GetFullPath(output).Equals(Path.GetFullPath(project.Source), StringComparison.OrdinalIgnoreCase) || File.Exists(output)) throw new IOException("VideoUpscale.OutputExists");
        var start = preview ? (long)Math.Round(project.PreviewStart * project.Media.Fps) : 0;
        var frames = preview ? Math.Min(project.Media.Frames - start, (long)Math.Round(Math.Min(5, project.PreviewLength) * project.Media.Fps)) : project.Media.Frames;
        if (start < 0 || frames <= 0) throw new InvalidDataException("VideoUpscale.InvalidRange");
        var job = new UpscaleJob { Project = UpscaleStore.Clone(project), Preview = preview, StartFrame = start, Frames = frames,
            ChunkFrames = Math.Max(4, (int)Math.Round(project.ChunkSeconds * project.Media.Fps)), Output = Path.GetFullPath(output), Fingerprint = UpscaleCatalog.Fingerprint(project) };
        project.LastJob = UpscaleStore.JobPath(job); job.Project.LastJob = project.LastJob;
        UpscaleStore.Save(UpscaleStore.JobPath(job), job); UpscaleStore.SaveProject(project);
        return job;
    }
    internal Task<string> Start(UpscaleJob job, string modelsRoot, IProgress<UpscaleProgress> progress, CancellationToken token, BackgroundOperationState? restored = null, string title = "Video upscale") =>
        ApplicationBackgroundOperations.RunAsync(Kind, title, UpscaleStore.ProjectPath(job.Project.Id), new UpscaleCheckpoint(UpscaleStore.JobPath(job)),
            ct => Run(UpscaleStore.Load<UpscaleJob>(UpscaleStore.JobPath(job)), modelsRoot, progress, ct), token, restored);
    private async Task<string> Run(UpscaleJob job, string modelsRoot, IProgress<UpscaleProgress> progress, CancellationToken token)
    {
        UpscaleCatalog.Validate(job.Project);
        if (!Guid.TryParseExact(job.Id, "N", out _) || job.ChunkFrames is < 1 or > 5000 || job.Frames < 1 || job.Fingerprint != UpscaleCatalog.Fingerprint(job.Project)) throw new InvalidDataException("VideoUpscale.InvalidProject");
        var project = job.Project; project.Workspace = UpscaleStore.Work(project.Id); var jobFile = UpscaleStore.JobPath(job); var root = Path.GetDirectoryName(jobFile)!;
        if (job.FinalArtifact is {} final && final.Path == job.Output && await UpscaleMediaTools.Valid(final, token)) {
            job.Complete = true; UpscaleStore.Save(jobFile, job); return job.Output;
        }
        if (File.Exists(job.Output)) throw new IOException("VideoUpscale.OutputExists");
        using var sourceLock = new FileStream(project.Source, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (sourceLock.Length != project.SourceLength || await UpscaleMediaTools.Hash(project.Source, token) != project.SourceSha256) throw new IOException("VideoUpscale.SourceChanged");
        await UpscaleFfmpeg.PrepareAsync(token);
        var cards = UpscaleComponents.Cards(modelsRoot, project);
        if (cards.Count > 0) using (var preparation = new UpscaleComponents())
            if (!await preparation.PrepareAsync(modelsRoot, project, false, null, token)) throw new IOException("VideoUpscale.ComponentsRequired");
        var steps = project.Steps.Where(s => s.Enabled).ToArray(); var count = (int)((job.Frames + job.ChunkFrames - 1) / job.ChunkFrames);
        var estimatedPart = steps.Max(s => (long)s.Width * s.Height) * Math.Min(job.ChunkFrames + 32, job.Frames) * 5 * 3;
        var drive = new DriveInfo(Path.GetPathRoot(root)!);
        if (drive.AvailableFreeSpace < estimatedPart + 512L * 1024 * 1024) throw new IOException("VideoUpscale.DiskSpace");
        job.Components = cards.Select(c => (object)new OutputModelUse(c.ModelArtifactId, c.DisplayName, c.Revision, "video processing")).ToList();
        ApplicationBackgroundOperations.Current?.SaveCheckpoint(new UpscaleCheckpoint(jobFile));
        List<string> results = [];
        for (var part = 0; part < count; part++) {
            token.ThrowIfCancellationRequested();
            var finalPart = Path.Combine(root, $"{part:D6}-ready.mkv");
            if (await Reuse($"{part}/ready", finalPart)) { results.Add(finalPart); continue; }
            if (drive.AvailableFreeSpace < estimatedPart + 512L * 1024 * 1024) throw new IOException("VideoUpscale.DiskSpace");
            var at = job.StartFrame + (long)part * job.ChunkFrames; var length = Math.Min(job.ChunkFrames, job.Frames - (long)part * job.ChunkFrames);
            // Shared overlap traverses the same whole chain, then is trimmed once. No duplicate frames in the export.
            var lead = Math.Min(16, at); var tail = Math.Min(16, project.Media.Frames - at - length); var total = lead + length + tail;
            var input = Path.Combine(root, $"{part:D6}-source.mkv");
            if (!await Reuse($"{part}/source", input)) {
                progress.Report(new(part / (double)count, $"{part + 1}/{count} · VideoUpscale.Decoding"));
                await UpscaleMediaTools.Cut(project.Source, input, project.Media, at - lead, total, token);
                await Checkpoint($"{part}/source", input, total, project.Media.Width, project.Media.Height);
            }
            for (var index = 0; index < steps.Length; index++) {
                var step = steps[index]; var output = Path.Combine(root, $"{part:D6}-step-{index:D3}.mkv"); var key = $"{part}/{step.Id}";
                if (!await Reuse(key, output)) {
                    progress.Report(new((part + index / (double)steps.Length) / count, $"{part + 1}/{count} · {index + 1}/{steps.Length} · {step.Engine}"));
                    await UpscaleNative.Run(step, input, output, cards, token, line => {
                        if (line.Length < 1000) progress.Report(new((part + index / (double)steps.Length) / count, line));
                    });
                    if (step.Mix < 1) {
                        var mixed = output + ".mix.mkv";
                        await UpscaleFfmpeg.RunAsync(false, ["-v", "error", "-y", "-i", input, "-i", output, "-filter_complex",
                            $"[0:v]{UpscaleMediaTools.ScaleFilter(step.Width, step.Height)}[original];[original][1:v]blend=all_expr='A*(1-{UpscaleMediaTools.N(step.Mix)})+B*{UpscaleMediaTools.N(step.Mix)}'", .. UpscaleMediaTools.Lossless, mixed], token);
                        File.Move(mixed, output, true);
                    }
                    await Checkpoint(key, output, total, step.Width, step.Height);
                }
                input = output;
            }
            if (!await Reuse($"{part}/ready", finalPart)) {
                await UpscaleFfmpeg.RunAsync(false, ["-v", "error", "-y", "-i", input, "-vf", $"trim=start_frame={lead}:end_frame={lead + length},setpts=PTS-STARTPTS",
                    "-an", .. Encode(project), finalPart], token);
                await Checkpoint($"{part}/ready", finalPart, length, steps[^1].Width, steps[^1].Height);
            }
            results.Add(finalPart);
            // A validated, encoded part is the durable recovery boundary. Retaining every 4K
            // lossless stage for an entire film would need terabytes without improving recovery.
            var obsolete = job.Parts.Where(p => p.Key.StartsWith(part + "/", StringComparison.Ordinal) && p.Key != part + "/ready").ToArray();
            foreach (var entry in obsolete) job.Parts.Remove(entry.Key);
            UpscaleStore.Save(jobFile, job);
            foreach (var entry in obsolete) {
                var path = Path.GetFullPath(entry.Value.Path);
                if (!path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unsafe intermediate path.");
                Lopata.Updates.SafeUpdatePath.RejectLinks(path); File.Delete(path);
            }
        }
        return await UpscaleAssembly.Publish(job, results, false, progress, token);

        async Task<bool> Reuse(string key, string expected) {
            if (!job.Parts.TryGetValue(key, out var saved)) return false;
            if (saved.Path != expected) throw new InvalidDataException("VideoUpscale.CheckpointDamaged");
            if (!await UpscaleMediaTools.Valid(saved, token)) {
                // Resuming is explicit: reconstruct this damaged stage, retaining all other
                // independently verified parts. Never accept its existence as success.
                job.Parts.Remove(key); UpscaleStore.Save(jobFile, job);
                progress.Report(new(0, "VideoUpscale.RebuildingPart")); return false;
            }
            return true;
        }
        async Task Checkpoint(string key, string file, long frames, int w, int h) {
            job.Parts[key] = await UpscaleMediaTools.Check(file, frames, w, h, token); UpscaleStore.Save(jobFile, job);
        }
    }
    private static string[] Encode(UpscaleProject p) => ["-c:v", p.Codec, .. (p.Codec == "ffv1" ? new[] { "-level", "3", "-pix_fmt", "gbrp16le" }
        : new[] { "-b:v", p.BitrateMbps + "M", "-bf", "0", "-pix_fmt", "yuv420p" }), "-threads", p.CpuThreads.ToString()];
}
