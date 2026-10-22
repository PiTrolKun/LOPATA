using System.Windows;
using AIHub.Models;
using AIHub.Services;

namespace AIHub;

public partial class MainWindow
{
    private static bool _componentAuditStartupAnnounced;
    private bool _componentAuditSubscribed;

    internal static bool ShouldRemindComponentAudit(ApplicationBehaviorSettings settings) =>
        settings.ComponentAuditEnabled && !settings.ComponentAuditEverDisabled;

    internal static void ApplyComponentAuditChoice(ApplicationBehaviorSettings settings, bool enabled)
    {
        settings.ComponentAuditEnabled = enabled;
        if (!enabled) settings.ComponentAuditEverDisabled = true;
    }

    private void InitializeComponentAuditSettings()
    {
        ComponentAudit.Localize = L;
        ComponentAudit.Configure(_appSettings.Behavior.ComponentAuditEnabled);
    }

    private void InitializeComponentAudit()
    {
        if (_componentAuditSubscribed) return;
        _componentAuditSubscribed = true;
        ComponentAuditButton.EnableRequested += EnableComponentAudit;
        ComponentAudit.Changed += ComponentAuditChanged;
        ComponentAuditFailures.Failure += ComponentAuditFailed;
        RefreshComponentAudit();
        // CompleteApplicationStartup announces after startup layout/checks have settled.
    }

    private void ComponentAuditButton_Click(object sender, RoutedEventArgs e) => SetComponentAudit(!ComponentAudit.Enabled);
    private void EnableComponentAudit() => SetComponentAudit(true);

    private void SetComponentAudit(bool enabled)
    {
        var previous = _appSettings.Behavior.ComponentAuditEnabled;
        var everDisabled = _appSettings.Behavior.ComponentAuditEverDisabled;
        try {
            ApplyComponentAuditChoice(_appSettings.Behavior, enabled);
            _appSettingsStore.Save(_appSettings);
            ComponentAudit.Configure(enabled); RefreshComponentAudit();
        }
        catch (Exception error) {
            _appSettings.Behavior.ComponentAuditEnabled = previous;
            _appSettings.Behavior.ComponentAuditEverDisabled = everDisabled;
            OwnedProcessRegistry.Log("component_audit_preference_save_failed", "Application", detail: error.GetType().Name);
            RefreshComponentAudit(); ComponentAuditButton.ShowSaveFailure();
        }
    }

    private void RefreshComponentAudit()
    {
        ComponentAudit.Localize = L;
        ComponentAuditButton.Localize(L); ComponentAuditButton.ApplyState(ComponentAudit.Enabled);
    }

    private void ComponentAuditChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(RefreshComponentAudit));
    }

    private void AnnounceComponentAuditStartup()
    {
        if (_componentAuditStartupAnnounced || !ShouldRemindComponentAudit(_appSettings.Behavior)) return;
        _componentAuditStartupAnnounced = true;
        if (!IsVisible || WindowState == WindowState.Minimized) {
            if (_applicationTray?.Notify(L("ComponentAudit.Title"), L("ComponentAudit.StartupMessage"), onOpen: () =>
                Dispatcher.BeginInvoke(new Action(() => { RestoreFromTray(); ComponentAuditButton.ShowStartupNotice(); }))) == true) return;
        }
        ComponentAuditButton.ShowStartupNotice();
    }

    private void ComponentAuditFailed(Exception error)
    {
        if (ComponentAudit.Enabled || !ComponentAuditFailures.IsEligible(error) || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(new Action(() => {
            if (ComponentAudit.Enabled || _processShutdownPending || _processShutdownComplete) return;
            if (!IsVisible || WindowState == WindowState.Minimized) {
                if (_applicationTray?.Notify(L("ComponentAudit.Title"), L("ComponentAudit.FailureMessage"), warning: true, onOpen: () =>
                    Dispatcher.BeginInvoke(new Action(() => { RestoreFromTray(); ComponentAuditButton.ShowFailureNotice(); }))) == true) return;
            }
            ComponentAuditButton.ShowFailureNotice();
        }));
    }

    private void DisposeComponentAudit()
    {
        if (!_componentAuditSubscribed) return;
        _componentAuditSubscribed = false;
        ComponentAudit.Changed -= ComponentAuditChanged;
        ComponentAuditFailures.Failure -= ComponentAuditFailed;
        ComponentAuditButton.EnableRequested -= EnableComponentAudit;
        ComponentAuditButton.HideNotice();
    }
}
