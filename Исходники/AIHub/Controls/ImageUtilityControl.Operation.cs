using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Controls;

public sealed partial class ImageUtilityControl
{
    private sealed record Checkpoint(string JobId);
    private bool HasPendingOperation() => ApplicationBackgroundOperations.Current is { HasPending: true, State.Kind: BackgroundKind };
    public void RefreshBackgroundStatus()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(RefreshBackgroundStatus); return; }
        var state = ApplicationBackgroundOperations.Current?.State;
        var ours = state?.Kind == BackgroundKind;
        var paused = ours && state?.Phase is BackgroundOperationPhase.Paused or BackgroundOperationPhase.Pausing or BackgroundOperationPhase.Waiting;
        var pending = HasPendingOperation();
        if (_walker is not null) _walker.Running = _busy && !paused;
        if (_start is not null) _start.IsEnabled = !IsBusy && !pending && _job.Items.Any(x => x.Status == ImageUtilityItemStatus.Pending);
        if (_pause is not null) { _pause.IsEnabled = pending; _pause.Content = paused ? "▶" : "⏸"; _pause.ToolTip = L(paused ? "Continue" : "Pause"); System.Windows.Automation.AutomationProperties.SetName(_pause, L(paused ? "Continue" : "Pause")); }
        if (_stop is not null) _stop.IsEnabled = IsBusy || pending;
        if (_retry is not null) _retry.IsEnabled = !IsBusy && !pending && _job.Items.Any(x => x.Status is ImageUtilityItemStatus.Failed or ImageUtilityItemStatus.Cancelled);
        if (_primaryPanel is not null) _primaryPanel.IsEnabled = !IsBusy && !pending;
        if (_settingsPanel is not null) _settingsPanel.IsEnabled = !IsBusy && !pending;
        if (_sourceButtons is not null) _sourceButtons.IsEnabled = !IsBusy && !pending;
        // Folder enumeration adds items on a worker thread. Avoid iterating that list during import.
        var complete = _adding ? 0 : _job.Items.Count(x => x.Status == ImageUtilityItemStatus.Completed);
        var total = _adding ? _job.Items.Count : _job.Items.Count(x => x.Status != ImageUtilityItemStatus.Duplicate);
        if (_status is not null) _status.Text = _adding ? L("Adding") : paused ? L("Paused") : _busy
            ? string.Format(CultureInfo.CurrentCulture, L("ProcessingCount"), complete, total)
            : _job.Finished ? string.Format(CultureInfo.CurrentCulture, L("CompletedCount"), complete, total) : L("Ready");
        if (_aiStatus is not null)
        {
            _aiStatus.Visibility = !Options.FormatOnly && ImageUtilityCatalog.GetMethod(Options.MethodId).IsAi ? Visibility.Visible : Visibility.Collapsed;
            _aiStatus.Text = L(ImageUtilityCatalog.GetMethod(Options.MethodId).NameKey) + " · " + L(paused ? "Paused" : _busy ? "AiProcessing" : _selectedAiReady ? "Installed" : "NeedsDownload");
        }
        WorkspaceChanged?.Invoke();
    }
    private IProgress<ImageUtilityProgress> Progress() => new Progress<ImageUtilityProgress>(value =>
    {
        var message = L(value.MessageKey);
        if (value.Arguments is { Length: > 0 })
            try { message = string.Format(CultureInfo.CurrentCulture, message, value.Arguments); }
            catch (FormatException) { message += " " + string.Join(" · ", value.Arguments); }
        AppendLog(message, value.IsError, value.MessageKey.EndsWith("Completed", StringComparison.Ordinal) || value.MessageKey.EndsWith("Finished", StringComparison.Ordinal));
        if (_progress is not null && value.Fraction is { } fraction)
        {
            if (value.MessageKey == "ImageUtility.Ai.Processing")
            {
                var count = _job.Items.Count(x => x.Status != ImageUtilityItemStatus.Duplicate);
                var done = _job.Items.Count(x => x.Status is ImageUtilityItemStatus.Completed or ImageUtilityItemStatus.Failed or ImageUtilityItemStatus.Skipped);
                fraction = count > 0 ? (done + Math.Clamp(fraction, 0, 1)) / count : 0;
            }
            _progress.IsIndeterminate = false; _progress.Value = Math.Clamp(fraction, 0, 1);
        }
        if (!_adding) RefreshRows();
    });
    private async Task StartAsync()
    {
        if (IsBusy || HasPendingOperation()) return;
        if (string.IsNullOrWhiteSpace(Options.ExportFolder)) throw new ImageUtilityException("ImageUtility.ChooseOutputRequired");
        if (!Options.FormatOnly && ImageUtilityCatalog.GetMethod(Options.MethodId).IsAi && !_ai.IsReady(Options.MethodId))
        { await ChooseMethodAsync(); return; }
        if (_job.Finished) BeginNewBatch(_job.Items.Where(x => x.Status is ImageUtilityItemStatus.Pending or ImageUtilityItemStatus.Cancelled));
        _job.Finished = false; SavePreferences(); _store.SaveJob(_job);
        await ExecuteAsync(CancellationToken.None);
    }
    private async Task ExecuteAsync(CancellationToken token, BackgroundOperationState? restored = null)
    {
        _busy = true; _cancel = CancellationTokenSource.CreateLinkedTokenSource(token); RefreshBackgroundStatus();
        try
        {
            await ApplicationBackgroundOperations.RunAsync(BackgroundKind, L("Title"), _job.Id, new Checkpoint(_job.Id), async attempt =>
            {
                if (!Options.FormatOnly && ImageUtilityCatalog.GetMethod(Options.MethodId).IsAi) await ApplicationBackgroundOperations.RetireModelsAsync();
                await new ImageUtilityQueue(_processor, _store).RunAsync(_job, Progress(), attempt);
                return true;
            }, _cancel.Token, restored);
            AppendLog(L("Finished"), success: true);
        }
        catch (OperationCanceledException) { AppendLog(L(ApplicationBackgroundOperations.ExitToken.IsCancellationRequested ? "Paused" : "Stopped")); }
        catch (Exception error) { Error(error); }
        finally { _busy = false; _cancel.Dispose(); _cancel = null; RefreshRows(true); }
    }
    private async Task PauseResumeAsync()
    {
        var controller = ApplicationBackgroundOperations.Current;
        if (controller?.State?.Kind != BackgroundKind) return;
        if (controller.State.Phase is BackgroundOperationPhase.Running or BackgroundOperationPhase.Pausing) await controller.PauseAsync();
        else await controller.ResumeAsync(ApplicationBackgroundOperations.ExitToken);
        RefreshBackgroundStatus();
    }
    private Task StopAsync()
    {
        if (_adding) { _importCancel?.Cancel(); RefreshBackgroundStatus(); return Task.CompletedTask; }
        _cancel?.Cancel();
        _importCancel?.Cancel();
        if (ApplicationBackgroundOperations.Current is { IsRunning: false, State.Kind: BackgroundKind } controller) controller.DiscardPending();
        RefreshBackgroundStatus(); return Task.CompletedTask;
    }
    private async Task RetryFailedAsync()
    {
        if (IsBusy || HasPendingOperation()) return;
        if (_job.Finished) BeginNewBatch(_job.Items.Where(x => x.Status is ImageUtilityItemStatus.Failed or ImageUtilityItemStatus.Cancelled));
        foreach (var item in _job.Items.Where(x => x.Status is ImageUtilityItemStatus.Failed or ImageUtilityItemStatus.Cancelled))
        { item.Status = ImageUtilityItemStatus.Pending; item.Attempts = 0; item.Error = item.ErrorKey = null; }
        _job.Finished = false; RefreshRows(); await StartAsync();
    }
    private void BeginNewBatch(IEnumerable<ImageUtilityItem> inputs)
    {
        // The completed job remains in the durable store. New options and outputs belong to a new process.
        _previousOutputFolder = _job.OutputFolder ?? _previousOutputFolder;
        var items = JsonSerializer.Deserialize<List<ImageUtilityItem>>(JsonSerializer.Serialize(inputs.ToArray()))!;
        foreach (var item in items)
        {
            item.Id = Guid.NewGuid().ToString("N"); item.Status = ImageUtilityItemStatus.Pending; item.Attempts = 0;
            item.OutputPath = item.PlannedOutputPath = item.PreparedOutputSha256 = null;
            item.OutputWidth = item.OutputHeight = 0; item.Error = item.ErrorKey = null;
        }
        _job = new() { Options = Clone(Options), Items = items };
        if (_progress is not null) _progress.Value = 0;
        RefreshRows(true);
    }
    public void Restore(string jobId)
    {
        if (_busy) return;
        if (Path.GetExtension(jobId).Equals(".json", StringComparison.OrdinalIgnoreCase)) jobId = Path.GetFileNameWithoutExtension(jobId);
        _job = _store.LoadJob(jobId) ?? throw new InvalidDataException(L("MissingJob")); Render(); RefreshRows(true);
    }
    public async Task ResumeAsync(BackgroundOperationState state, CancellationToken token)
    {
        var checkpoint = state.Input.Deserialize<Checkpoint>() ?? throw new InvalidDataException(L("MissingJob"));
        Restore(checkpoint.JobId); await ExecuteAsync(token, state);
    }
}
