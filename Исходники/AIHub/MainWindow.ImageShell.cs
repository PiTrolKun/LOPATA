using System.Diagnostics;
using System.IO;
using AIHub.Models;
using AIHub.Services;

namespace AIHub;

public partial class MainWindow
{
    private readonly ImageShellQueueStore _imageShellInbox = new(Path.Combine(AppDataPaths.BaseDirectory, "image-shell"));
    private bool _imageShellPumping;

    internal void AcceptImageShellRequest(ImageShellRequest request)
    {
        // This synchronous write is the IPC acceptance boundary. A failed write must not acknowledge delivery.
        _imageShellInbox.Enqueue(request);
        if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(() => _ = PumpImageShellAsync());
    }

    private void RegisterImageShellBackgroundOperation()
    {
        _backgroundOperations!.Register(ImageShellQueueRunner.BackgroundKind, async (state, token) =>
        {
            var job = _imageShellInbox.Load(state.Project ?? "") ?? throw new InvalidDataException("Missing shell request.");
            await RunImageShellAsync(job, token, state);
        });
        _backgroundOperations.Changed += () =>
        {
            if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(() => _ = PumpImageShellAsync());
        };
    }

    private async Task PumpImageShellAsync()
    {
        if (!_applicationReady || _imageShellPumping || _backgroundLifetime.IsCancellationRequested
            || _backgroundOperations?.HasPending == true) return;
        _imageShellPumping = true;
        try
        {
            while (!_backgroundLifetime.IsCancellationRequested && _backgroundOperations?.HasPending != true)
            {
                var job = _imageShellInbox.Pending().FirstOrDefault();
                if (job is null) break;
                await RunImageShellAsync(job, _backgroundLifetime.Token);
            }
        }
        catch (OperationCanceledException) when (_backgroundLifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            OwnedProcessRegistry.Log("shell_queue_waiting", "ImageShell", detail: error.GetType().Name);
            _applicationTray?.Notify(L("ImageShell.Title"), L("ImageShell.Failed"), warning: true);
        }
        finally { _imageShellPumping = false; }
    }

    private async Task RunImageShellAsync(ImageShellJob job, CancellationToken token, BackgroundOperationState? restored = null)
    {
        var processor = new ImageShellProcessor();
        var queue = new ImageShellQueueRunner(_imageShellInbox, async (request, path, directory, cancellation) =>
            (await processor.ProcessAsync(request, path, directory, cancellation)).OutputPath);
        await ApplicationBackgroundOperations.RunAsync(ImageShellQueueRunner.BackgroundKind,
            L(job.Request.Operation == ImageShellOperation.WebP ? "ImageShell.WebP" : "ImageShell.Upscale2"),
            job.Request.RequestId, new { job.Request.RequestId }, async attempt =>
            { await queue.RunAsync(job, attempt); return true; }, token, restored);
        if (job.Items.Any(item => item.Status == ImageUtilityItemStatus.Failed))
            _applicationTray?.Notify(L("ImageShell.Title"), L("ImageShell.Failed"), warning: true,
                onOpen: () => Dispatcher.BeginInvoke(() => ViewImageShellResult(job.Request.RequestId)));
    }

    private void ViewImageShellResult(string id)
    {
        var job = _imageShellInbox.Load(id);
        if (job is null) return;
        var path = job.Items.Select(item => item.OutputPath ?? item.Path).FirstOrDefault(File.Exists);
        if (path is null) return;
        Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, Arguments = "/select,\"" + path + "\"" });
    }
}
