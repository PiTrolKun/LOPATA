using AIHub.Models;

namespace AIHub.Services;

public sealed class ImageShellQueueRunner
{
    public const string BackgroundKind = "image-shell";
    private readonly ImageShellQueueStore _store;
    private readonly Func<ImageShellRequest, string, string, CancellationToken, Task<string>> _process;
    public ImageShellQueueRunner(ImageShellQueueStore store,
        Func<ImageShellRequest, string, string, CancellationToken, Task<string>> process)
    { _store = store; _process = process; }

    public async Task RunAsync(ImageShellJob job, CancellationToken token)
    {
        using var lease = _store.TryLease(job.Request.RequestId)
            ?? throw new InvalidOperationException("This shell request is already being processed.");
        // Another installation may have completed the inbox entry before this lease was acquired.
        var current = _store.Load(job.Request.RequestId);
        if (current?.Finished == true) { job.Finished = true; job.Items = current.Items; return; }
        if (current is not null) job.Items = current.Items;
        foreach (var item in job.Items)
        {
            token.ThrowIfCancellationRequested();
            if (item.Status is ImageUtilityItemStatus.Completed or ImageUtilityItemStatus.Failed) continue;
            if (item.Status == ImageUtilityItemStatus.Running) item.Attempts = Math.Max(0, item.Attempts - 1);
            while (item.Attempts < 4)
            {
                token.ThrowIfCancellationRequested();
                item.Status = ImageUtilityItemStatus.Running; item.Attempts++; item.Error = null; _store.Save(job);
                try
                {
                    item.OutputPath = await _process(job.Request, item.Path, _store.Transactions(job.Request.RequestId), token);
                    item.Status = ImageUtilityItemStatus.Completed; _store.Save(job); break;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    item.Status = ImageUtilityItemStatus.Pending; item.Attempts = Math.Max(0, item.Attempts - 1);
                    _store.Save(job); throw;
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    item.Error = error.Message;
                    item.Status = item.Attempts >= 4 ? ImageUtilityItemStatus.Failed : ImageUtilityItemStatus.Pending;
                    _store.Save(job);
                    OwnedProcessRegistry.Log("shell_item_failed", "ImageShell", detail: error.GetType().Name);
                    if (item.Status == ImageUtilityItemStatus.Failed) break;
                    await Task.Delay(250 * item.Attempts, token);
                }
            }
        }
        job.Finished = true; _store.Save(job);
    }
}
