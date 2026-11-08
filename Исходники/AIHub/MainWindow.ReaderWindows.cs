using System.Reflection;
using System.Windows;
using AIHub.Services;
using AIHub.Services.OfficeReader;
using AIHub.Controls.OfficeReader;

namespace AIHub;

public partial class MainWindow
{
    private System.Windows.Controls.CheckBox? _readerShellSwitch;
    private ReaderShellIntegration? ReaderRegistration() =>
        Assembly.GetEntryAssembly()?.GetName().Name == typeof(MainWindow).Assembly.GetName().Name
            && Environment.ProcessPath is { } path ? new(path) : null;

    internal void ApplyReaderIntegration()
    {
        try { ReaderRegistration()?.Apply(_appSettings.FileViewer.ReaderWindowsIntegrationEnabled, L("Reader.Title"), L("Reader.Windows.Description"),
            projectTypeName: L("OfficeEditor.Project")); }
        catch (Exception error)
        {
            OwnedProcessRegistry.Log("reader_shell_registration_failed", "WindowsIntegration", detail: error.GetType().Name);
            StatusText.Text = L("WindowsIntegration.Failed");
        }
    }

    private void SetReaderIntegration(bool enabled)
    {
        if (_refreshingSettingsWorkspace) return;
        var previous = _appSettings.FileViewer.ReaderWindowsIntegrationEnabled;
        try
        {
            ReaderRegistration()?.Apply(enabled, L("Reader.Title"), L("Reader.Windows.Description"), projectTypeName: L("OfficeEditor.Project"));
            _appSettings.FileViewer.ReaderWindowsIntegrationEnabled = enabled; _appSettingsStore.Save(_appSettings);
        }
        catch (Exception error)
        {
            _appSettings.FileViewer.ReaderWindowsIntegrationEnabled = previous;
            OwnedProcessRegistry.Log("reader_shell_setting_failed", "WindowsIntegration", detail: error.GetType().Name);
            _refreshingSettingsWorkspace = true;
            try { if (_readerShellSwitch is not null) _readerShellSwitch.IsChecked = previous; }
            finally { _refreshingSettingsWorkspace = false; }
            System.Windows.MessageBox.Show(this, L("WindowsIntegration.Failed"), L("Settings.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenReaderWindowsSettings() => new ReaderWindowsSettingsWindow(this, L, _isDarkTheme,
        _appSettings.FileViewer, () => _appSettingsStore.Save(_appSettings), ReaderRegistration()?.RegisteredName).ShowDialog();
}
