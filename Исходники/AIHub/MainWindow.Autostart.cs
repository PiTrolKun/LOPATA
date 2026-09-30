using System.Windows;
using Lopata.Updates;
using AIHub.Services;

namespace AIHub;

public partial class MainWindow
{
    private StartupRegistration? _startupRegistration;

    private void RefreshAutostartSetting()
    {
        if (_autostartSwitch is null) return;
        var installation = InstalledUpdateState.Read();
        var installed = AppDataPaths.ProjectRoot is null && installation is not null
            && System.IO.Path.GetFullPath(installation.AppDirectory).TrimEnd(System.IO.Path.DirectorySeparatorChar)
                .Equals(AppContext.BaseDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        _startupRegistration = installed ? new(InstalledUpdateState.LauncherPath) : null;
        _autostartSwitch.IsEnabled = installed;
        try { _autostartSwitch.IsChecked = _startupRegistration?.IsEnabled == true; }
        catch (Exception error)
        {
            _autostartSwitch.IsEnabled = false; _autostartSwitch.IsChecked = false;
            OwnedProcessRegistry.Log("autostart_read_failed", "Application", detail: error.GetType().Name);
        }
    }

    private void SetAutostart(bool enabled)
    {
        if (_refreshingSettingsWorkspace || _startupRegistration is null) return;
        StartupRegistration.Snapshot? previous = null;
        var previousSetting = _appSettings.Behavior.LaunchWithWindows;
        try
        {
            previous = _startupRegistration.Capture();
            _startupRegistration.SetEnabled(enabled);
            _appSettings.Behavior.LaunchWithWindows = enabled;
            _appSettingsStore.Save(_appSettings);
        }
        catch (Exception)
        {
            _appSettings.Behavior.LaunchWithWindows = previousSetting;
            try { if (previous is not null) _startupRegistration.Restore(previous); } catch { /* Report failure; do not claim success. */ }
            _refreshingSettingsWorkspace = true;
            try { RefreshAutostartSetting(); } finally { _refreshingSettingsWorkspace = false; }
            System.Windows.MessageBox.Show(this, L("Settings.Behavior.AutostartFailed"), L("Settings.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
