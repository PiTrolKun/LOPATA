using System.Windows;
using AIHub.Services;

namespace AIHub.Controls;

public sealed partial class LiteraryWorkspaceControl
{
    private bool _readinessShown, _readinessOpen;
    private TaskCompletionSource? _readinessAccepted;
    private LiteraryReadinessWindow? _readinessWindow;
    private void ScheduleWorkspaceStart()
    {
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (!IsLoaded || !IsVisible || _projectMissing || _readinessOpen) return;
            _readinessOpen = true;
            _readinessAccepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task preparation = Task.CompletedTask;
            var preparationStarted = false;
            try
            {
                var telemetry = new LiteraryGpuTelemetry();
                var window = new LiteraryReadinessWindow(_l, telemetry.ReadAsync, () => _runtime.RecommendedGpuSpareBytes)
                    { Owner = Window.GetWindow(this) };
                _readinessWindow = window;
                window.SetPreparationStatus(_l("Literary.Rag.ProjectWait"));
                // Render the notice before starting even the preparation's synchronous prefix.
                window.ContentRendered += (_, _) =>
                {
                    if (preparationStarted) return;
                    preparationStarted = true; preparation = PrepareMemoryAsync();
                };
                var accepted = window.ShowDialog() == true;
                _readinessWindow = null;
                if (accepted)
                {
                    _readinessShown = true;
                    _readinessAccepted.TrySetResult();
                    if (!preparationStarted) { preparationStarted = true; preparation = PrepareMemoryAsync(); }
                }
                else
                {
                    _memoryCancellation?.Cancel();
                    _readinessAccepted.TrySetCanceled();
                }
                // Do not retire the workspace/runtime while its preparation still owns them.
                await preparation;
                if (!IsVisible || !IsLoaded || window.Owner is { IsVisible: false }) return;
                if (!accepted && CanLeave()) HomeRequested?.Invoke();
            }
            finally
            {
                _readinessWindow = null; _readinessAccepted = null; _readinessOpen = false;
            }
            if (_readinessShown && _memoryRetry.Visibility != Visibility.Visible) ScheduleReadinessAndCalibration();
        }));
    }
    private async Task WaitForReadinessAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_readinessAccepted is { } gate) await gate.Task.WaitAsync(token);
        token.ThrowIfCancellationRequested();
    }
    private void ScheduleReadinessAndCalibration()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!IsLoaded || !IsVisible || Indexing || _runtime.IsBusy || _projectMissing || _readinessOpen) return;
            if (_readinessShown) ScheduleFirstCalibration();
        }));
    }
}
