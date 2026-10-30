using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIHub.Services.VideoEditor;

namespace AIHub.Services.VideoPreparation;

/// <summary>Owns a native Comfy VACE worker and accepts only independently verified complete checkpoints.</summary>
internal static class VideoVaceRunner
{
    internal static string DirectoryFor(string id) => Guid.TryParseExact(id, "N", out _)
        ? Path.Combine(AppDataPaths.BaseDirectory, "VideoPreparation", "VaceJobs", id)
        : throw new InvalidDataException("VideoPrep.InvalidArchive");

    internal static Task RunAsync(VideoVaceJob job, VideoPreparationProject project, Func<Task> commit,
        Action<string> log, CancellationToken token, bool probeOnly = false, ManagedModelLibraryStore? library = null) =>
        VideoVaceRecovery.RunAsync(job.Run, job.Settings.RetryCount,
            elapsed => RunAttemptAsync(job, project, commit, log, token, elapsed, probeOnly, library), commit, log, token);

    private static async Task RunAttemptAsync(VideoVaceJob job, VideoPreparationProject project, Func<Task> commit,
        Action<string> log, CancellationToken token, Func<double> elapsed, bool probeOnly, ManagedModelLibraryStore? library)
    {
        using var audit = ComponentAudit.CaptureOperation(); using var auditLog = ComponentAudit.CaptureLog(log);
        var scripts = new[] { ("vace_worker.py", "AIHub.VaceWorker"), ("vace_native.py", "AIHub.VaceNative"), ("vace_media.py", "AIHub.VaceMedia") };
        var pins = new List<PinnedTreeFile>();
        foreach (var (name, resource) in scripts)
        {
            using var source = typeof(VideoVaceRunner).Assembly.GetManifestResourceStream(resource)
                ?? throw new IOException("VideoPrep.VaceRuntimeRequired");
            pins.Add(new(name, source.Length, Convert.ToHexString(SHA256.HashData(source))));
        }
        await RuntimeFileAudit.VerifyAsync("Video.VACE.Adapter", Path.Combine(AppContext.BaseDirectory, "Tools"), ApplicationBuildVersion.Value, pins, token, log);
        var environment = await VideoVaceRuntime.PrepareAsync(job.ModelsRoot, job.Connections, token, log, library);
        await VideoRuntime.PrepareAsync(token);
        var directory = DirectoryFor(job.Run.Id); var output = Path.Combine(directory, "output"); Directory.CreateDirectory(output);
        foreach (var folder in new[] { "input", "temp" }) Directory.CreateDirectory(Path.Combine(directory, folder));
        await RestoreFiles(project, job.Run, output, token);
        var models = Models(environment);
        var facts = new OutputProvenance { Scenario = "video-generation/wan-vace-1.3b", ModelHistory = "recorded-inference", Models = models };
        // A native crash after the whole event must not repeat verified assembly/encoding.
        if (!probeOnly && job.Run.Results.LastOrDefault() is { } completed)
        {
            if (!CompleteCoverage(job)) throw Invalid();
            var result = job.Run.Files.Single(f => f.Role == completed.VideoRole);
            await ProbeAsync(RuntimeFileAudit.Contained(output, result.RelativePath), job.Run.Takes.Sum(t => t.Frames - t.PrefixFrames), models, token);
            job.Run.Complete = true; await commit(); return;
        }
        var modelRoot = Path.Combine(environment.Cards.Single(c => c.ModelArtifactId == VideoVaceCatalog.ComponentId).InstallDirectory, "split_files");
        var payload = new
        {
            job.Run.Id, job.Run.InputHash, job.Settings, job.Items, job.Run.Takes, Probe = probeOnly,
            ElapsedBeforeAttempt = elapsed(), Output = output, Input = Path.Combine(directory, "input"), Temp = Path.Combine(directory, "temp"),
            Provenance = facts, Environment = new { environment.Libraries, environment.Comfy, environment.Python.Device },
            ModelFolders = new Dictionary<string, string> { ["diffusion_models"] = Path.Combine(modelRoot, "diffusion_models"),
                ["text_encoders"] = Path.Combine(modelRoot, "text_encoders"), ["vae"] = Path.Combine(modelRoot, "vae") },
            Models = new { Diffusion = "wan2.1_vace_1.3B_fp16.safetensors", Encoder = "umt5_xxl_fp8_e4m3fn_scaled.safetensors", Vae = "wan_2.1_vae.safetensors" }
        };
        var request = Path.Combine(directory, "request.json"); await File.WriteAllTextAsync(request, JsonSerializer.Serialize(payload), token);
        var info = new ProcessStartInfo(environment.Python.Python) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        ManagedPythonLaunch.ScriptArguments(info, [Path.Combine(AppContext.BaseDirectory, "Tools", "vace_worker.py"), request]);
        using var process = OwnedProcessRegistry.Shared.Start(info, "Video.VACE.Generate");
        using var cancelled = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var errors = new StringBuilder();
        var stderr = Task.Run(async () =>
        {
            try
            {
                await using var file = new StreamWriter(Path.Combine(directory, "worker-stderr.log"), append: true, Encoding.UTF8);
                while (await process.StandardError.ReadLineAsync() is { } line)
                {
                    await file.WriteLineAsync(line); await file.FlushAsync(); log(line); errors.AppendLine(line);
                    if (errors.Length > 20000) errors.Remove(0, errors.Length - 12000);
                }
            }
            catch { if (!process.HasExited) process.Kill(true); throw; }
        });
        string? workerError = null; var done = false;
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (!line.StartsWith("@vace ", StringComparison.Ordinal)) { log(line); continue; }
                using var json = JsonDocument.Parse(line[6..]); var message = json.RootElement; var kind = message.GetProperty("kind").GetString();
                log("[VACE event] " + line[6..]);
                log("[VACE event] " + JsonSerializer.Serialize(new { kind = "elapsed", elapsed_seconds = elapsed() }));
                await File.AppendAllTextAsync(Path.Combine(directory, "events.jsonl"),
                    JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, message }) + "\n", CancellationToken.None);
                if (kind == "error") workerError = message.GetProperty("error").GetString();
                else if (kind == "take")
                {
                    var take = message.GetProperty("take").Deserialize<VideoVaceTake>() ?? throw Invalid();
                    await AcceptTake(job, project, output, message, take, models, CancellationToken.None);
                    job.Run.RetryAttempts = 0; job.Run.GenerationSeconds = elapsed(); await commit();
                }
                else if (kind == "whole")
                {
                    if (!CompleteCoverage(job)) throw Invalid();
                    var video = message.GetProperty("video").GetString() ?? throw Invalid();
                    var frames = message.GetProperty("frames").GetInt32();
                    if (frames != job.Run.Takes.Sum(t => t.Frames - t.PrefixFrames)) throw Invalid();
                    await ProbeAsync(RuntimeFileAudit.Contained(output, video), frames, models, CancellationToken.None);
                    await AcceptFiles(message, job.Run, project, output, CancellationToken.None);
                    var file = job.Run.Files.SingleOrDefault(f => f.RelativePath == video) ?? throw Invalid();
                    if (!job.Run.Results.Any(r => r.VideoRole == file.Role))
                        job.Run.Results.Add(new(Guid.NewGuid().ToString("N"), file.Role, frames / 16d, DateTimeOffset.UtcNow, models));
                    job.Run.GenerationSeconds = elapsed(); await commit();
                }
                else if (kind == "done")
                {
                    if (!message.GetProperty("complete").GetBoolean() || job.Run.Results.Count == 0 || !CompleteCoverage(job)) throw Invalid();
                    job.Run.Complete = true; done = true; await commit();
                }
                else if (kind == "probe" && probeOnly) done = true;
            }
            await process.WaitForExitAsync(CancellationToken.None); await stderr; token.ThrowIfCancellationRequested();
            if (process.ExitCode == unchecked((int)0xC0000005)) throw new VideoVaceWorkerCrashException("VideoPrep.VaceRuntimeFailed: " + errors);
            if (process.ExitCode != 0 || !done)
                throw new IOException(workerError?.StartsWith("VideoPrep.", StringComparison.Ordinal) == true
                    ? workerError : "VideoPrep.VaceRuntimeFailed: " + workerError + "\n" + errors);
        }
        finally { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); await stderr; }
    }

    internal static bool CompleteCoverage(VideoVaceJob job)
    {
        if (job.Items is null || job.Items.Length == 0 || job.Run.Takes is null) return false;
        var required = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var item in job.Items)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Id) || !double.IsFinite(item.Seconds) || item.Seconds <= 0) return false;
            var frames = Math.Ceiling(item.Seconds * VideoVaceWorkspaceState.Fps);
            if (!double.IsFinite(frames) || frames >= long.MaxValue || !required.TryAdd(item.Id, (long)frames)) return false;
        }
        var delivered = required.Keys.ToDictionary(id => id, _ => 0L, StringComparer.Ordinal);
        foreach (var take in job.Run.Takes)
        {
            if (take is null || string.IsNullOrWhiteSpace(take.ItemId) || !VideoVaceWorkspaceState.FrameGrid(take.Frames) || take.PrefixFrames < 0
                || take.PrefixFrames >= take.Frames || !delivered.ContainsKey(take.ItemId)) return false;
            delivered[take.ItemId] = checked(delivered[take.ItemId] + take.Frames - take.PrefixFrames);
        }
        return required.All(item => delivered[item.Key] >= item.Value);
    }

    private static async Task AcceptTake(VideoVaceJob job, VideoPreparationProject project, string output,
        JsonElement message, VideoVaceTake take, OutputModelUse[] models, CancellationToken token)
    {
        var previous = job.Run.Takes.LastOrDefault();
        if (string.IsNullOrWhiteSpace(take.Id) || job.Run.Takes.Any(t => t.Id == take.Id) || !VideoVaceRun.SafeRelative(take.Video)
            || !job.Items.Any(i => i.Id == take.ItemId) || take.Parent != (previous?.Id ?? "") || take.Frames is < 5 or > 81
            || take.Frames > job.Settings.Frames || (take.Frames - 1) % 4 != 0 || take.PrefixFrames >= take.Frames
            || take.PrefixFrames != (previous is null ? 0 : Math.Min(job.Settings.ContextFrames, previous.Frames))) throw Invalid();
        await ProbeAsync(RuntimeFileAudit.Contained(output, take.Video), take.Frames, models, token);
        var lineageRelative = Path.ChangeExtension(take.Video, ".lineage.json").Replace('\\', '/');
        var lineagePath = RuntimeFileAudit.Contained(output, lineageRelative);
        Lopata.Updates.SafeUpdatePath.RejectLinks(lineagePath);
        if (new FileInfo(lineagePath).Length > 65536) throw Invalid();
        using var lineage = JsonDocument.Parse(await File.ReadAllTextAsync(lineagePath, token)); var entry = lineage.RootElement;
        if (entry.GetProperty("Run").GetString() != job.Run.Id || entry.GetProperty("InputHash").GetString() != job.Run.InputHash
            || entry.GetProperty("Fps").GetInt32() != 16 || entry.GetProperty("Take").Deserialize<VideoVaceTake>() != take
            || entry.GetProperty("ContextMode").GetString() != "native-vace-firstclip-mask") throw Invalid();
        var parentSha = entry.GetProperty("ParentVideoSha256").GetString(); var prefixSha = entry.GetProperty("PrefixRgbSha256").GetString();
        if (previous is null ? parentSha != "" || prefixSha != "" : !string.Equals(parentSha, job.Run.Files.Single(f => f.RelativePath == previous.Video).Sha256, StringComparison.OrdinalIgnoreCase)
            || prefixSha is null || prefixSha.Length != 64 || !prefixSha.All(char.IsAsciiHexDigit)) throw Invalid();
        var files = message.GetProperty("files").Deserialize<WorkerFile[]>() ?? throw Invalid();
        if (!files.Any(f => f.RelativePath == take.Video) || !files.Any(f => f.RelativePath == lineageRelative)) throw Invalid();
        await AcceptFiles(message, job.Run, project, output, token); job.Run.Takes.Add(take);
    }

    private static async Task ProbeAsync(string path, int frames, OutputModelUse[] expected, CancellationToken token)
    {
        Lopata.Updates.SafeUpdatePath.RejectLinks(path);
        using var probe = await VideoRuntime.ProbeAsync(path, token); var root = probe.RootElement;
        var stream = root.GetProperty("streams").EnumerateArray().SingleOrDefault(s => s.GetProperty("codec_type").GetString() == "video");
        var tags = root.GetProperty("format").GetProperty("tags");
        var tag = tags.EnumerateObject().SingleOrDefault(p => p.Name.Equals("LOPATA_VIDEO", StringComparison.OrdinalIgnoreCase));
        var facts = tag.Value.ValueKind == JsonValueKind.String ? JsonSerializer.Deserialize<OutputProvenance>(tag.Value.GetString()!) : null;
        if (stream.ValueKind != JsonValueKind.Object || stream.GetProperty("avg_frame_rate").GetString() != "16/1"
            || !stream.TryGetProperty("nb_frames", out var count) || !int.TryParse(count.GetString(), out var actual) || actual != frames
            || stream.GetProperty("width").GetInt32() <= 0 || stream.GetProperty("height").GetInt32() <= 0
            || facts?.Program != "LOPATA" || facts.Scenario != "video-generation/wan-vace-1.3b" || facts.GenerationSeconds is not { } elapsed
            || !double.IsFinite(elapsed) || elapsed < 0 || facts.Models.Length != expected.Length
            || expected.Any(e => !facts.Models.Contains(e))) throw Invalid();
    }

    private sealed record WorkerFile(string RelativePath, long Size, string Sha256);
    private static async Task AcceptFiles(JsonElement message, VideoVaceRun run, VideoPreparationProject project, string output, CancellationToken token)
    {
        var files = message.GetProperty("files").Deserialize<WorkerFile[]>() ?? throw Invalid();
        if (files.Length is < 1 or > 4 || files.Select(f => f.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Length) throw Invalid();
        foreach (var file in files)
        {
            if (!VideoVaceRun.SafeRelative(file.RelativePath)) throw Invalid();
            await VerifyFile(RuntimeFileAudit.Contained(output, file.RelativePath), file.Size, file.Sha256, token);
            if (run.Files.Any(f => f.RelativePath == file.RelativePath)) throw Invalid();
        }
        foreach (var file in files)
        {
            var role = "vace-run/" + run.Id + "/" + Guid.NewGuid().ToString("N");
            project.Attachments.Add(new(RuntimeFileAudit.Contained(output, file.RelativePath), role));
            run.Files.Add(new(file.RelativePath, role, file.Size, file.Sha256));
        }
    }

    internal static async Task RestoreFiles(VideoPreparationProject project, VideoVaceRun run, string output, CancellationToken token)
    {
        foreach (var file in run.Files)
        {
            var source = project.Attachments.Single(a => a.Role == file.Role).Path; var target = RuntimeFileAudit.Contained(output, file.RelativePath);
            await VerifyFile(source, file.Size, file.Sha256, token);
            if (Path.GetFullPath(source).Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) continue;
            if (File.Exists(target)) { await VerifyFile(target, file.Size, file.Sha256, token); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); var staging = target + "." + Guid.NewGuid().ToString("N") + ".restore";
            try
            {
                await using (var input = File.OpenRead(source))
                await using (var destination = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
                    await input.CopyToAsync(destination, token);
                await VerifyFile(staging, file.Size, file.Sha256, token); token.ThrowIfCancellationRequested(); File.Move(staging, target);
            }
            finally { if (File.Exists(staging)) File.Delete(staging); }
        }
    }

    private static async Task VerifyFile(string path, long size, string sha, CancellationToken token)
    {
        if (size <= 0 || sha is null || sha.Length != 64 || !sha.All(char.IsAsciiHexDigit)) throw Invalid();
        Lopata.Updates.SafeUpdatePath.RejectLinks(path); await using var input = File.OpenRead(path);
        if (input.Length != size || !Convert.ToHexString(await SHA256.HashDataAsync(input, token)).Equals(sha, StringComparison.OrdinalIgnoreCase)) throw Invalid();
    }

    private static OutputModelUse[] Models(VideoVaceEnvironment environment)
    {
        var weights = environment.Cards.Single(c => c.ModelArtifactId == VideoVaceCatalog.ComponentId);
        var names = new[] { ("wan2.1_vace_1.3B_fp16.safetensors", "video diffusion and temporal conditioning"),
            ("umt5_xxl_fp8_e4m3fn_scaled.safetensors", "text encoder"), ("wan_2.1_vae.safetensors", "video encoder and decoder") };
        return [.. names.Select(n => new OutputModelUse(weights.ModelArtifactId + "/" + n.Item1, n.Item1, weights.Revision, n.Item2)),
            .. environment.Cards.Where(c => c.ModelArtifactId != VideoVaceCatalog.ComponentId).Select(c => new OutputModelUse(c.ModelArtifactId, c.DisplayName, c.Revision, "runtime"))];
    }
    private static InvalidDataException Invalid() => new("VideoPrep.VaceInvalidResult");
}
