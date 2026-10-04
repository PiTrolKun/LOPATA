using System.IO;
using System.Security.Cryptography;
using AIHub.Models;

namespace AIHub.Services;

public sealed class ImageUtilityQueue
{
    public const string BackgroundKind = "image-utility";
    private readonly IImageUtilityProcessor _processor;
    private readonly ImageUtilityStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ImageUtilityQueue(IImageUtilityProcessor processor, ImageUtilityStore store)
    { _processor = processor; _store = store; }

    public async Task RunAsync(ImageUtilityJob job, IProgress<ImageUtilityProgress>? progress = null, CancellationToken token = default)
    {
        if (!await _gate.WaitAsync(0, token)) throw new InvalidOperationException("An image utility job is already running.");
        try
        {
            if (string.IsNullOrWhiteSpace(job.Options.ExportFolder)) throw new ImageUtilityException("ImageUtility.Error.OutputFolder");
            ImageUtilitySources.RecheckDuplicates(job);
            PrepareOutputFolder(job);
            job.Finished = false;
            _store.SaveJob(job);
            foreach (var item in job.Items)
            {
                token.ThrowIfCancellationRequested();
                if (item.Status is ImageUtilityItemStatus.Completed or ImageUtilityItemStatus.Duplicate
                    or ImageUtilityItemStatus.Failed or ImageUtilityItemStatus.Skipped) continue;
                if (await RecoverPublishedAsync(item, token))
                {
                    _store.SaveJob(job);
                    progress?.Report(new("ImageUtility.Event.Recovered", [item.DisplayName], Fraction(job), item.Id));
                    continue;
                }
                // A running checkpoint represents an interrupted attempt, not a processing error.
                if (item.Status == ImageUtilityItemStatus.Running) item.Attempts = Math.Max(0, item.Attempts - 1);
                while (item.Attempts < 4)
                {
                    token.ThrowIfCancellationRequested();
                    item.Status = ImageUtilityItemStatus.Running;
                    item.Attempts++;
                    item.Error = null; item.ErrorKey = null;
                    _store.SaveJob(job);
                    progress?.Report(new("ImageUtility.Event.Started", [item.DisplayName, item.Attempts], Fraction(job), item.Id));
                    try
                    {
                        item.OutputPath = await _processor.ProcessAsync(item, job.Options, job.OutputFolder!, progress, token,
                            () => _store.SaveJob(job));
                        // Persist completion even if pause arrived just after the atomic file publication.
                        item.Status = ImageUtilityItemStatus.Completed;
                        _store.SaveJob(job);
                        progress?.Report(new("ImageUtility.Event.Completed", [item.DisplayName, item.OutputPath], Fraction(job), item.Id));
                        break;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        item.Status = ImageUtilityItemStatus.Pending;
                        item.Attempts = Math.Max(0, item.Attempts - 1);
                        _store.SaveJob(job);
                        throw;
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        item.Error = ex.Message;
                        item.ErrorKey = (ex as ImageUtilityException)?.MessageKey ?? "ImageUtility.Error.Processing";
                        var skip = item.ErrorKey is "ImageUtility.Error.AiAnimation" or "ImageUtility.Error.AnimationFormat"
                            or "ImageUtility.Error.MultiPage" or "ImageUtility.Error.ApngUnsupported";
                        item.Status = skip ? ImageUtilityItemStatus.Skipped
                            : item.Attempts >= 4 ? ImageUtilityItemStatus.Failed : ImageUtilityItemStatus.Pending;
                        _store.SaveJob(job);
                        progress?.Report(new(item.Status == ImageUtilityItemStatus.Pending ? "ImageUtility.Event.Retry" : "ImageUtility.Event.Failed",
                            [item.DisplayName, item.Attempts], Fraction(job), item.Id, true));
                        progress?.Report(new(item.ErrorKey, [item.Error == item.ErrorKey ? "" : item.Error], Fraction(job), item.Id, true));
                        if (item.Status is ImageUtilityItemStatus.Failed or ImageUtilityItemStatus.Skipped) break;
                        await Task.Delay(TimeSpan.FromMilliseconds(300 * item.Attempts), token);
                    }
                }
            }
            job.Finished = true;
            _store.SaveJob(job);
            progress?.Report(new("ImageUtility.Event.Finished",
                [job.Items.Count(x => x.Status == ImageUtilityItemStatus.Completed), job.Items.Count(x => x.Status == ImageUtilityItemStatus.Failed)], 1));
        }
        finally { _gate.Release(); }
    }

    public static void RetryFailed(ImageUtilityJob job)
    {
        foreach (var item in job.Items.Where(x => x.Status is ImageUtilityItemStatus.Failed or ImageUtilityItemStatus.Skipped))
        {
            item.Status = ImageUtilityItemStatus.Pending;
            item.Attempts = 0; item.Error = null; item.ErrorKey = null;
        }
        job.Finished = false;
    }

    private void PrepareOutputFolder(ImageUtilityJob job)
    {
        if (job.OutputFolder is not null) { Directory.CreateDirectory(job.OutputFolder); return; }
        var root = Path.GetFullPath(job.Options.ExportFolder);
        Directory.CreateDirectory(root);
        if (job.Items.Count(x => x.Status is ImageUtilityItemStatus.Pending or ImageUtilityItemStatus.Running or ImageUtilityItemStatus.Cancelled) > 1)
        {
            do
            {
                var number = _store.ReserveProcessNumber();
                var folder = $"{job.StartedAt:yyyy-MM-dd}_Апскейл_{number:D6}";
                job.OutputFolder = Path.Combine(root, folder);
            } while (Directory.Exists(job.OutputFolder) || File.Exists(job.OutputFolder));
        }
        else job.OutputFolder = root;
        Directory.CreateDirectory(job.OutputFolder);
    }

    private static async Task<bool> RecoverPublishedAsync(ImageUtilityItem item, CancellationToken token)
    {
        if (item.PlannedOutputPath is null || item.PreparedOutputSha256 is null || !File.Exists(item.PlannedOutputPath)) return false;
        await using var file = File.OpenRead(item.PlannedOutputPath);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, token));
        if (!hash.Equals(item.PreparedOutputSha256, StringComparison.OrdinalIgnoreCase)) return false;
        item.OutputPath = item.PlannedOutputPath;
        item.Status = ImageUtilityItemStatus.Completed;
        item.Error = null; item.ErrorKey = null;
        return true;
    }

    private static double Fraction(ImageUtilityJob job)
    {
        var total = job.Items.Count(x => x.Status != ImageUtilityItemStatus.Duplicate);
        return total == 0 ? 0 : job.Items.Count(x => x.Status is ImageUtilityItemStatus.Completed or ImageUtilityItemStatus.Failed or ImageUtilityItemStatus.Skipped) / (double)total;
    }
}
