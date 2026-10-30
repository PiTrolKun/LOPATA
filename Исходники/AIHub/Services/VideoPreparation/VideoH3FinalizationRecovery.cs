using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace AIHub.Services.VideoPreparation;

/// <summary>Reuse verified completed takes after an adapter-only finalization repair.</summary>
internal static class VideoH3FinalizationRecovery
{
    internal static async Task<VideoH3Job> CreateAsync(VideoH3Job recorded, VideoH3Job fresh,
        VideoPreparationProject project, CancellationToken token)
    {
        const string invalid = "VideoPrep.H3ChangedProject";
        project.Validate();
        var prior = VideoH3WorkspaceState.Ensure(project, recorded.ModelId).Runs.SingleOrDefault(r => r.Id == recorded.Run.Id);
        if (recorded.ModelId != fresh.ModelId || recorded.ProjectId != project.Id || fresh.ProjectId != project.Id || prior is null
            || prior.InputHash != recorded.Run.InputHash || prior.Complete || prior.Results.Count != 0
            || prior.Takes.Count == 0 || fresh.Run.Id == prior.Id || fresh.Run.Complete
            || fresh.Run.Results.Count != 0
            || fresh.Run.SourceRunId != prior.Id && (fresh.Run.Takes.Count != 0 || fresh.Run.Files.Count != 0)
            || !Path.GetFullPath(recorded.ModelsRoot).Equals(Path.GetFullPath(fresh.ModelsRoot), StringComparison.OrdinalIgnoreCase)
            || Settings(recorded.Settings) != Settings(fresh.Settings)
            || recorded.Items.Length != fresh.Items.Length)
            throw new InvalidDataException(invalid);

        for (var index = 0; index < fresh.Items.Length; index++)
        {
            var before = recorded.Items[index]; var after = fresh.Items[index];
            if (JsonSerializer.Serialize(before with { Images = [] }) != JsonSerializer.Serialize(after with { Images = [] })
                || before.Images.Length != after.Images.Length) throw new InvalidDataException(invalid);
            for (var image = 0; image < before.Images.Length; image++)
            {
                var original = await Hash(before.Images[image], token);
                var current = await Hash(after.Images[image], token);
                if (!CryptographicOperations.FixedTimeEquals(original, current))
                    throw new InvalidDataException(invalid);
            }
        }

        // This is a conservative host-side check. The worker repeats the check
        // using native sidecar seam resolution and refuses any new generation.
        var items = fresh.Items.ToDictionary(i => i.Id, StringComparer.Ordinal);
        var videos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var take in prior.Takes)
        {
            if (!items.TryGetValue(take.ItemId, out var item) || item.SceneId != take.SceneId || !VideoH3Run.SafeRelative(take.Video)
                || !take.Video.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
                || !double.IsFinite(take.Seconds) || take.Seconds <= 0
                || take.Parent.Length != 0 && !videos.Contains(take.Parent)
                || !videos.Add(take.Video)) throw new InvalidDataException(invalid);
        }
        foreach (var item in fresh.Items)
            if (prior.Takes.Where(t => t.ItemId == item.Id).Sum(t => t.Seconds) + 1d / 24 < item.Seconds)
                throw new InvalidDataException("VideoPrep.H3FinalizationIncomplete");

        var required = prior.Takes.SelectMany(t => new[] { t.Video,
            Path.ChangeExtension(t.Video, ".mctx.safetensors").Replace('\\', '/'),
            Path.ChangeExtension(t.Video, ".cond.safetensors").Replace('\\', '/') }).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = prior.Files.Where(f => required.Contains(f.RelativePath)).ToArray();
        if (files.Length != required.Count || files.Select(f => f.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != required.Count)
            throw new InvalidDataException("VideoPrep.H3InvalidResult");
        var verified = new List<(VideoH3Artifact File, string Path)>();
        foreach (var file in files)
        {
            if (!VideoH3Run.SafeRelative(file.RelativePath)
                || !file.Role.StartsWith("h3-run/" + prior.Id + "/", StringComparison.Ordinal))
                throw new InvalidDataException("VideoPrep.H3InvalidResult");
            var attachment = project.Attachments.SingleOrDefault(a => a.Role == file.Role)
                ?? throw new InvalidDataException("VideoPrep.MissingAttachment");
            Lopata.Updates.SafeUpdatePath.RejectLinks(attachment.Path);
            await using var source = File.OpenRead(attachment.Path);
            if (source.Length != file.Size || !Convert.ToHexString(await SHA256.HashDataAsync(source, token)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("VideoPrep.H3InvalidResult");
            verified.Add((file, attachment.Path));
        }

        // No transferred references are published until every input and saved
        // artifact has passed validation. The old run remains historical.
        token.ThrowIfCancellationRequested();
        if (fresh.Run.SourceRunId == prior.Id)
        {
            // The archive checkpoint can survive a crash before the controller
            // persists its replacement input. Revalidate that already committed
            // transfer rather than creating duplicate roles or losing the run.
            if (JsonSerializer.Serialize(fresh.Run.Takes) != JsonSerializer.Serialize(prior.Takes)
                || required.Any(path => !fresh.Run.Files.Any(f => f.RelativePath == path))
                || fresh.Run.Files.Any(f => !required.Contains(f.RelativePath) && !f.RelativePath.StartsWith("refinement-cache/", StringComparison.Ordinal))
                || prior.GenerationSeconds is { } elapsed && (fresh.Run.GenerationSeconds is not { } recovered || recovered < elapsed))
                throw new InvalidDataException(invalid);
            foreach (var file in fresh.Run.Files)
            {
                var original = files.SingleOrDefault(f => f.RelativePath == file.RelativePath);
                var attachment = project.Attachments.SingleOrDefault(a => a.Role == file.Role);
                if (original is not null && (original.Size != file.Size || original.Sha256 != file.Sha256) || attachment is null
                    || !file.Role.StartsWith("h3-run/" + fresh.Run.Id + "/", StringComparison.Ordinal))
                    throw new InvalidDataException("VideoPrep.H3InvalidResult");
                Lopata.Updates.SafeUpdatePath.RejectLinks(attachment.Path);
                await using var source = File.OpenRead(attachment.Path);
                if (source.Length != file.Size || !Convert.ToHexString(await SHA256.HashDataAsync(source, token)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("VideoPrep.H3InvalidResult");
            }
            return fresh with { FinalizeOnly = true };
        }
        foreach (var (file, path) in verified)
        {
            var role = "h3-run/" + fresh.Run.Id + "/" + Guid.NewGuid().ToString("N");
            project.Attachments.Add(new(path, role));
            fresh.Run.Files.Add(file with { Role = role });
        }
        fresh.Run.Takes.AddRange(prior.Takes);
        fresh.Run.GenerationSeconds = prior.GenerationSeconds;
        fresh.Run.SourceRunId = prior.Id;
        return fresh with { FinalizeOnly = true };
    }

    private static string Settings(VideoH3Settings settings)
    {
        var copy = VideoShots.Copy(settings); copy.ExportName = ""; copy.RetryCount = 3;
        return JsonSerializer.Serialize(copy);
    }
    private static async Task<byte[]> Hash(string path, CancellationToken token)
    {
        Lopata.Updates.SafeUpdatePath.RejectLinks(path);
        await using var source = File.OpenRead(path);
        return await SHA256.HashDataAsync(source, token);
    }
}
