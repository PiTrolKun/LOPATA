using System.Windows;
using AIHub.Services;

namespace AIHub;

public partial class MainWindow
{
    private ApplicationTrayController? _applicationTray;
    private bool _fullExitRequested, _sessionEnding;
    private WindowState _stateBeforeTray = WindowState.Normal;
    private ApplicationCloseChoiceWindow? _closeChoiceWindow;

    private void InitializeApplicationTray()
    {
        var application = System.Windows.Application.Current;
        // An independent updates window must not keep an already closed main application alive.
        application.ShutdownMode = ShutdownMode.OnMainWindowClose;
        application.SessionEnding += ApplicationSessionEnding;
        void OnUi(Action action) { if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(action); }
        _applicationTray = ApplicationTrayController.TryCreate(L,
            () => OnUi(RestoreFromTray),
            () => OnUi(() => { RestoreFromTray(); ShowSettingsPage(); }),
            () => OnUi(OpenApplicationUpdates),
            () => OnUi(() => { RestoreFromTray(); _fullExitRequested = true; Close(); }),
            () => OnUi(() => _ = ChangeBackgroundOperationAsync()),
            () => OnUi(ViewBackgroundResult));
        _applicationTray?.AddCaptureCommands(() => OnUi(OpenCaptureFolder),
            () => OnUi(() => _ = TakeScreenshotAsync(AIHub.Models.CaptureSource.Monitor)), () => OnUi(StopCaptureRecording));
        DesktopAttentionNotification.Notify = (title, message) => _applicationTray?.Notify(title, message, warning: true) == true;
    }

    private void ApplicationSessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        _sessionEnding = true;
        if (_gifTask is not null || _videoTask is not null)
        {
            StopCaptureRecording(); e.Cancel = true; _sessionEnding = false;
            StatusText.Text = L("Capture.GifExitWait"); return;
        }
        try
        {
            if (!LiteraryPage.CheckpointBackgroundState()) throw new System.IO.IOException("Literary checkpoint was not confirmed.");
            _backgroundOperations?.CheckpointForExit();
            SaveActiveSessionCheckpoint(strict: true); SaveCurrentWindowPlacement();
            _backgroundLifetime.Cancel();
        }
        catch (Exception error)
        {
            OwnedProcessRegistry.Log("session_end_checkpoint_failed", "Application", detail: error.GetType().Name);
            // Ask Windows to keep us alive if a durable checkpoint could not be confirmed.
            e.Cancel = true; _sessionEnding = false; StatusText.Text = L("Tray.ExitFailed");
        }
    }

    private bool TryResolveCloseBehavior(out bool closeToTray)
    {
        closeToTray = _appSettings.Behavior.CloseToTray;
        if (!ApplicationClosePolicy.ShouldAsk(_appSettings.Behavior.AskBeforeClosing, _fullExitRequested,
            _sessionEnding, _applicationUpdateStart is not null, _processShutdownComplete || _processShutdownPending)) return true;
        if (_closeChoiceWindow is not null) { _closeChoiceWindow.Activate(); return false; }
        var question = new ApplicationCloseChoiceWindow(this, L, _applicationTray?.IsAvailable == true);
        _closeChoiceWindow = question;
        try
        {
            if (question.ShowDialog() != true || question.CloseToTray is not bool choice) return false;
            closeToTray = choice;
            if (question.RememberChoice)
            {
                _appSettings.Behavior.CloseToTray = choice;
                _appSettings.Behavior.AskBeforeClosing = false;
                _appSettingsStore.Save(_appSettings);
                ApplySettingsWorkspaceLocalization();
            }
            return true;
        }
        finally { _closeChoiceWindow = null; }
    }

    private bool TryHideToTray(bool? closeToTray = null)
    {
        if (!ApplicationClosePolicy.ShouldHide(closeToTray ?? _appSettings.Behavior.CloseToTray, _applicationTray?.IsAvailable == true,
            _fullExitRequested, _sessionEnding, _applicationUpdateStart is not null, _processShutdownComplete || _processShutdownPending)) return false;
        SaveCurrentWindowPlacement();
        _stateBeforeTray = WindowState == WindowState.Minimized ? _lastNonMinimizedWindowState : WindowState;
        ApplicationBackgroundOperations.PreserveHiddenWork = true;
        KeepUpdatesIndependent(); Hide(); ScheduleBetaUpdateCheck(); return true;
    }

    private void RestoreFromTray()
    {
        ApplicationBackgroundOperations.PreserveHiddenWork = SettingsPage.Visibility == Visibility.Visible;
        if (!IsVisible) { Show(); WindowState = _stateBeforeTray; }
        if (WindowState == WindowState.Minimized) WindowState = _lastNonMinimizedWindowState;
        Activate();
        ScheduleBetaUpdateCheck();
    }

    private void KeepUpdatesIndependent()
    {
        if (_updateWindow is not { } window) return;
        window.Owner = null; window.ShowInTaskbar = true;
    }

    private void DisposeApplicationTray()
    {
        System.Windows.Application.Current.SessionEnding -= ApplicationSessionEnding;
        _applicationTray?.Dispose(); _applicationTray = null;
        DesktopAttentionNotification.Notify = null;
        _updateWindow?.Close();
    }
}
