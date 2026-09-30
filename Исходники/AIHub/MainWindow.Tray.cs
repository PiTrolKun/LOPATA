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
            () => OnUi(() => { RestoreFromTray(); _fullExitRequested = true; Close(); }));
    }

    private void ApplicationSessionEnding(object sender, SessionEndingCancelEventArgs e) => _sessionEnding = true;

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
        KeepUpdatesIndependent(); Hide(); return true;
    }

    private void RestoreFromTray()
    {
        if (!IsVisible) { Show(); WindowState = _stateBeforeTray; }
        if (WindowState == WindowState.Minimized) WindowState = _lastNonMinimizedWindowState;
        Activate();
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
        _updateWindow?.Close();
    }
}
