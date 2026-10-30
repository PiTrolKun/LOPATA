using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIHub.Services.VideoEditor;

namespace AIHub.Services.VideoPreparation;

internal static class VideoH3Runner
{
    internal static string DirectoryFor(string id) => Guid.TryParseExact(id, "N", out _)
        ? Path.Combine(AppDataPaths.BaseDirectory, "VideoPreparation", "H3Jobs", id)
        : throw new InvalidDataException("VideoPrep.InvalidArchive");
    internal static async Task RunAsync(VideoH3Job job, VideoPreparationProject project, Func<Task> commit,
        Action<string> log, CancellationToken token, bool probeOnly = false, ManagedModelLibraryStore? library = null)
    {
        await VideoH3Recovery.RunAsync(job.Run, job.Settings.RetryCount,
            elapsed => RunAttemptAsync(job, project, commit, log, token, elapsed, probeOnly, library), commit, log, token);
    }
    private static async Task RunAttemptAsync(VideoH3Job job, VideoPreparationProject project, Func<Task> commit,
        Action<string> log, CancellationToken token, Func<double> elapsed, bool probeOnly, ManagedModelLibraryStore? library)
    {
        using var audit = ComponentAudit.CaptureOperation(); using var auditLog = ComponentAudit.CaptureLog(log);
        var scripts = new[] { ("h3_worker.py", "AIHub.H3Worker"), ("h3_native.py", "AIHub.H3Native"), ("h3_memory.py", "AIHub.H3Memory"), ("h3_assemble.py", "AIHub.H3Assemble"), ("h3_refinement_cache.py", "AIHub.H3RefinementCache") };
        var files = new List<PinnedTreeFile>();
        foreach (var (name, resource) in scripts)
        {
            using var input = typeof(VideoH3Runner).Assembly.GetManifestResourceStream(resource)!;
            var size = input.Length; files.Add(new(name, size, Convert.ToHexString(SHA256.HashData(input))));
        }
        await RuntimeFileAudit.VerifyAsync("Video.H3.Adapter", Path.Combine(AppContext.BaseDirectory, "Tools"), ApplicationBuildVersion.Value, files, token, log);
        var lite = job.ModelId == VideoH3LiteCatalog.ModelId;
        _ = VideoH3WorkspaceState.Get(project, job.ModelId);
        var environment = await VideoH3Runtime.PrepareAsync(job.ModelsRoot, job.Connections, token, log, library, lite);
        await VideoRuntime.PrepareAsync(token);
        var directory = DirectoryFor(job.Run.Id); var output = Path.Combine(directory, "output"); Directory.CreateDirectory(output);
        foreach (var folder in new[] { "input", "temp" }) Directory.CreateDirectory(Path.Combine(directory, folder));
        await RestoreFiles(project, job.Run, output, token);
        var models = lite ? VideoH3Profile.Facts(environment) : Models(environment, false);
        if (job.FinalizeOnly)
            await VerifyFinalizationModels(job.Run, output, models, token);
        var facts = new OutputProvenance { Scenario = "video-generation/minimax-h3", ModelHistory = "recorded-inference", Models = models };

        var payload = new
        {
            job.ModelId, job.Run.Id, job.Run.InputHash, job.Settings, job.Items, job.Run.Takes, job.FinalizeOnly, Probe = probeOnly,
            ElapsedBeforeAttempt = job.Run.GenerationSeconds is null ? (double?)null : elapsed(),
            WorkStartedAtUtc = DateTimeOffset.UtcNow,
            Output = output, Input = Path.Combine(directory, "input"), Temp = Path.Combine(directory, "temp"),
            Ffmpeg = Path.Combine(VideoRuntime.DirectoryPath, "ffmpeg.exe"), Provenance = facts,
            UpscaleProvenance = facts with { Models = lite ? models : Models(environment, true) },
            Environment = new { environment.Libraries, environment.Comfy, environment.Timeline, environment.Upscaler, environment.Spectrum, environment.Turbo, environment.ClipProj, environment.Python.Device },
            ModelFolders = VideoH3Profile.Folders(environment, lite),
            Models = VideoH3Profile.Models(lite)
        };
        var requestPath = Path.Combine(directory, "request.json"); await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(payload), token);
        var info = new ProcessStartInfo(environment.Python.Python) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        ManagedPythonLaunch.ScriptArguments(info, [Path.Combine(AppContext.BaseDirectory, "Tools", "h3_worker.py"), requestPath]);
        using var process = OwnedProcessRegistry.Shared.Start(info, "Video.H3.Generate");
        using var activity = new VideoH3GpuActivity(process.Id, log);
        using var cancelled = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var errors = new StringBuilder();
        var stderr = Task.Run(async () =>
        {
            try
            {
            await using var file = new StreamWriter(Path.Combine(directory, "worker-stderr.log"), append: true, Encoding.UTF8);
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                await file.WriteLineAsync(line); await file.FlushAsync(); log(line);
                errors.AppendLine(line); if (errors.Length > 20000) errors.Remove(0, errors.Length - 12000);
            }
            }
            catch { if (!process.HasExited) process.Kill(true); throw; }
        });
        string? workerError = null; var done = false;
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (!line.StartsWith("@h3 ", StringComparison.Ordinal)) { log(line); continue; }
                using var json = JsonDocument.Parse(line[4..]); var message = json.RootElement; var kind = message.GetProperty("kind").GetString();
                log("[H3 event] " + line[4..]);
                log("[H3 event] " + JsonSerializer.Serialize(new { kind = "elapsed", elapsed_seconds = elapsed() }));
                await File.AppendAllTextAsync(Path.Combine(directory, "events.jsonl"),
                    JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, message }) + "\n", CancellationToken.None);
                if (kind == "error") workerError = message.GetProperty("error").GetString();
                else if (kind == "memory") log("[H3] memory-" + message.GetProperty("loader").GetString());
                else if (kind == "stage") log("[H3] " + message.GetProperty("text").GetString());
                else if (kind == "progress") log($"[H3] {message.GetProperty("value")}/{message.GetProperty("total")}");
                else if (kind == "refinement-saved")
                {
                    await AcceptFiles(message, job.Run, project, output, CancellationToken.None);
                    if (job.Run.GenerationSeconds is not null) job.Run.GenerationSeconds = elapsed();
                    await commit();
                }
                else if (kind is "take" or "whole")
                {
                    await AcceptFiles(message, job.Run, project, output, CancellationToken.None);
                    string video;
                    if (kind == "take")
                    {
                        var take = message.GetProperty("take").Deserialize<VideoH3Take>()!;
                        if (!job.Run.Takes.Any(t => t.Id == take.Id)) job.Run.Takes.Add(take);
                        job.Run.RetryAttempts = 0;
                        video = take.Video;
                    }
                    else video = message.GetProperty("video").GetString()!;
                    using var checkedVideo = await VideoRuntime.ProbeAsync(RuntimeFileAudit.Contained(output, video), CancellationToken.None);
                    var format = checkedVideo.RootElement.GetProperty("format");
                    var tag = format.GetProperty("tags").EnumerateObject().SingleOrDefault(p => p.Name.Equals("LOPATA_VIDEO", StringComparison.OrdinalIgnoreCase));
                    var provenance = tag.Value.ValueKind == JsonValueKind.String ? JsonSerializer.Deserialize<OutputProvenance>(tag.Value.GetString()!) : null;
                    var stream = checkedVideo.RootElement.GetProperty("streams").EnumerateArray().FirstOrDefault(s => s.GetProperty("codec_type").GetString() == "video");
                    if (provenance?.Program != "LOPATA" || provenance.Scenario != "video-generation/minimax-h3" || provenance.Models.Length == 0
                        || stream.ValueKind != JsonValueKind.Object || stream.GetProperty("avg_frame_rate").GetString() != "24/1"
                        || stream.GetProperty("width").GetInt32() <= 0 || stream.GetProperty("height").GetInt32() <= 0)
                        throw new InvalidDataException("VideoPrep.H3InvalidResult");
                    var visible = kind == "whole" || job.Settings.RunMode == "review";
                    if (visible)
                    {
                        var file = job.Run.Files.Single(f => f.RelativePath == video);
                        var seconds = kind == "take" ? job.Run.Takes.Last().Seconds : message.GetProperty("seconds").GetDouble();
                        job.Run.Results.Add(new(Guid.NewGuid().ToString("N"), kind == "take" ? "fragment" : "whole", file.Role, seconds, DateTimeOffset.UtcNow,
                            kind == "whole" && job.Settings.Upscale ? Models(environment, true) : models));
                    }
                    if (job.Run.GenerationSeconds is not null) job.Run.GenerationSeconds = elapsed();
                    await commit();
                }
                else if (kind == "done") { done = true; job.Run.Complete = message.GetProperty("complete").GetBoolean(); await commit(); }
                else if (kind == "probe") done = true;
            }
            await process.WaitForExitAsync(CancellationToken.None); await stderr; token.ThrowIfCancellationRequested();
            if (process.ExitCode == unchecked((int)0xC0000005))
                throw new VideoH3WorkerCrashException("VideoPrep.H3RuntimeFailed: native access violation\n" + errors);
            if (process.ExitCode != 0 || !done)
                throw new IOException(workerError?.StartsWith("VideoPrep.", StringComparison.Ordinal) == true ? workerError
                    : "VideoPrep.H3RuntimeFailed: " + workerError + "\n" + errors);
        }
        finally { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); await stderr; }
    }
    private sealed record WorkerFile(string RelativePath, long Size, string Sha256);
    private static async Task VerifyFinalizationModels(VideoH3Run run, string output, OutputModelUse[] expected, CancellationToken token)
    {
        foreach (var take in run.Takes)
        {
            using var probe = await VideoRuntime.ProbeAsync(RuntimeFileAudit.Contained(output, take.Video), token);
            var tags = probe.RootElement.GetProperty("format").GetProperty("tags");
            var tag = tags.EnumerateObject().SingleOrDefault(p => p.Name.Equals("LOPATA_VIDEO", StringComparison.OrdinalIgnoreCase));
            var facts = tag.Value.ValueKind == JsonValueKind.String ? JsonSerializer.Deserialize<OutputProvenance>(tag.Value.GetString()!) : null;
            if (facts?.Program != "LOPATA" || facts.Scenario != "video-generation/minimax-h3"
                || facts.Models.Length != expected.Length || expected.Any(e => !facts.Models.Any(m => m.Id == e.Id && m.Revision == e.Revision && m.Role == e.Role)))
                throw new InvalidDataException("VideoPrep.H3ChangedProject");
        }
    }
    private static async Task AcceptFiles(JsonElement message, VideoH3Run run, VideoPreparationProject project, string output, CancellationToken token)
    {
        foreach (var file in message.GetProperty("files").Deserialize<WorkerFile[]>()!)
        {
            if (!VideoH3Run.SafeRelative(file.RelativePath)) throw new InvalidDataException("VideoPrep.H3InvalidResult");
            var path = RuntimeFileAudit.Contained(output, file.RelativePath);
            await VerifyFile(path, file.Size, file.Sha256, token);
            if (run.Files.Any(f => f.RelativePath == file.RelativePath)) continue;
            var role = "h3-run/" + run.Id + "/" + Guid.NewGuid().ToString("N");
            project.Attachments.Add(new(path, role)); run.Files.Add(new(file.RelativePath, role, file.Size, file.Sha256));
        }
    }
    internal static async Task RestoreFiles(VideoPreparationProject project, VideoH3Run run, string output, CancellationToken token)
    {
        foreach (var file in run.Files)
        {
            var source = project.Attachments.Single(a => a.Role == file.Role).Path;
            var target = RuntimeFileAudit.Contained(output, file.RelativePath);
            await VerifyFile(source, file.Size, file.Sha256, token);
            if (Path.GetFullPath(source).Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) continue;
            if (File.Exists(target)) { await VerifyFile(target, file.Size, file.Sha256, token); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var staging = target + "." + Guid.NewGuid().ToString("N") + ".restore";
            try
            {
                await using (var input = File.OpenRead(source))
                await using (var destination = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
                    await input.CopyToAsync(destination, token);
                await VerifyFile(staging, file.Size, file.Sha256, token);
                token.ThrowIfCancellationRequested(); File.Move(staging, target);
            }
            finally { if (File.Exists(staging)) File.Delete(staging); }
        }
    }
    private static async Task VerifyFile(string path, long size, string sha, CancellationToken token)
    {
        Lopata.Updates.SafeUpdatePath.RejectLinks(path); await using var input = File.OpenRead(path);
        if (input.Length != size || !Convert.ToHexString(await SHA256.HashDataAsync(input, token)).Equals(sha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("VideoPrep.H3InvalidResult");
    }
    private static OutputModelUse[] Models(VideoH3Environment environment, bool upscale)
    {
        var card = environment.Cards.Single(c => c.ModelArtifactId == "video-h3-models");
        var names = new Dictionary<string, string>
        {
            ["minimax_h3_ref2va_pruned_int8_convrot.safetensors"] = "video and audio diffusion",
            ["qwen3vl_32b_minimax_h3_int8_convrot.safetensors"] = "text and image encoder",
            ["minimax_h3_video_vae_int8_convrot.safetensors"] = "video decoder",
            ["minimax_h3_audio_vae_fp32.safetensors"] = "audio decoder"
        };
        if (upscale) names["minimax_h3_video_vae_fp16.safetensors"] = "upscaled video decoder";
        var facts = names.Select(n => new OutputModelUse(card.ModelArtifactId + "/" + n.Key, n.Key, card.Revision, n.Value)).ToList();
        facts.AddRange(environment.Cards.Where(c => c.ModelArtifactId is "video-h3-source-comfy" or "video-h3-source-timeline"
            || upscale && c.ModelArtifactId is "video-h3-upscale-model" or "video-h3-source-upscaler")
            .Select(c => new OutputModelUse(c.ModelArtifactId, c.DisplayName, c.Revision, c.ModelArtifactId == "video-h3-upscale-model" ? "latent upscaler" : "runtime")));
        return facts.ToArray();
    }
}
