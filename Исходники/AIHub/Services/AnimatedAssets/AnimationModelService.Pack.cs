using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services.VisualScenario;
using Lopata.Updates;

namespace AIHub.Services.AnimatedAssets;

internal sealed record AnimationPackDrawItem(string Target, string Prompt);

internal sealed partial class AnimationModelService
{
    private const string DrawPack = "draw-pack";
    private const string PackTarget = "pack";

    private sealed record PackPoseJob(string Id, string Target, string Prompt, int Seed);
    private sealed record PackProgress(string Id, string Target, int Completed, int Total, bool Finalized = false);

    internal async Task DrawPackAsync(string projectPath, byte[] image, IReadOnlyList<AnimationPackDrawItem> items,
        Func<AnimationModelResult, Task> apply, CancellationToken token = default)
    {
        var job = CreateJob(DrawPack, PackTarget, "", ArtistId, hasImage: true, transparent: false) with
        {
            Items = items.Select(item => new PackPoseJob(Guid.NewGuid().ToString("N"), item.Target, item.Prompt.Trim(),
                RandomNumberGenerator.GetInt32(int.MaxValue))).ToArray()
        };
        ValidatePack(projectPath, job);
        job = job with { Transparent = await StageImageAsync(job, image, token) };
        await RunPackAsync(projectPath, job, apply, token);
    }

    private async Task RunPackAsync(string projectPath, Job job, Func<AnimationModelResult, Task> apply,
        CancellationToken token, BackgroundOperationState? restored = null)
    {
        var items = ValidatePack(projectPath, job);
        var progress = restored?.Checkpoint is { } checkpoint
            ? checkpoint.Deserialize<PackProgress>() ?? throw new InvalidDataException("Visual.InvalidProject")
            : new PackProgress(job.Id, PackTarget, 0, items.Length);
        if (progress.Id != job.Id || progress.Target != PackTarget || progress.Total != items.Length
            || progress.Completed < 0 || progress.Completed > items.Length || progress.Finalized && progress.Completed != items.Length)
            throw new InvalidDataException("Visual.InvalidProject");

        var directory = JobDirectory(job);
        await ApplicationBackgroundOperations.RunAsync(OperationKind, _localize("Animation.Title"), projectPath, job,
            async ct =>
            {
                var source = Path.Combine(directory, "source.png");
                if (progress.Completed < items.Length)
                {
                    SafeUpdatePath.RejectLinks(source);
                    if (!File.Exists(source)) throw new BackgroundOperationFailedException("Visual.MissingImage");
                }
                for (var index = progress.Completed; index < items.Length; index++)
                {
                    ct.ThrowIfCancellationRequested();
                    var item = items[index];
                    var poseJob = job with { Action = Draw, Id = item.Id, Target = item.Target, Prompt = item.Prompt, Seed = item.Seed, Items = null };
                    var drawing = await RenderStillAsync(poseJob, directory, source, ct, recordBackgroundCheckpoint: false);
                    ct.ThrowIfCancellationRequested();
                    await apply(new(item.Target, Drawing: drawing));
                    SaveProgress(progress with { Completed = index + 1 });
                }
                ct.ThrowIfCancellationRequested();
                if (!progress.Finalized)
                {
                    await apply(new(PackTarget));
                    SaveProgress(progress with { Finalized = true });
                }
                return progress;
            }, token, restored);
        DeleteCompletedStage(directory);

        void SaveProgress(PackProgress point)
        {
            ApplicationBackgroundOperations.Current?.SaveCheckpoint(point);
            progress = point;
        }
    }

    private static PackPoseJob[] ValidatePack(string projectPath, Job job)
    {
        if (!Path.IsPathFullyQualified(projectPath) || job.Action != DrawPack || job.Target != PackTarget
            || !job.HasImage || job.Model is not (VisualComponents.Turbo or VisualComponents.Base)
            || job.Items is not { Length: > 0 } items)
            throw new InvalidDataException("Visual.InvalidProject");
        var ids = new HashSet<Guid>();
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item is null || !Guid.TryParseExact(item.Id, "N", out var id) || !ids.Add(id)
                || item.Seed < 0 || string.IsNullOrWhiteSpace(item.Prompt) || item.Target is null
                || item.Target.Split('/') is not ["card", var card, "pose", var pose]
                || !Guid.TryParseExact(card, "N", out _) || !Guid.TryParseExact(pose, "N", out _) || !targets.Add(item.Target))
                throw new InvalidDataException("Visual.InvalidProject");
        }
        return items;
    }
}
