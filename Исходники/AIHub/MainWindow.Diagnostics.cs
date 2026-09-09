using System.Diagnostics;
using System.IO;
using System.Windows;
using AIHub.Services;

namespace AIHub;

public partial class MainWindow
{
    private bool _refreshingDiagnostics;
    private void RefreshDetailedDiagnosticsSettings()
    {
        _refreshingDiagnostics = true;
        try
        {
            DetailedLiteraryDiagnosticsCheckBox.Content = L("Diagnostics.Literary.Enabled");
            DetailedLiteraryDiagnosticsCheckBox.IsChecked = _appSettings.DetailedLiteraryDiagnostics;
            DetailedLiteraryDiagnosticsHint.Text = L("Diagnostics.Literary.Hint");
            OpenLiteraryDiagnosticsButton.Content = L("Diagnostics.Literary.Open");
        }
        finally { _refreshingDiagnostics = false; }
    }
    private void DetailedLiteraryDiagnostics_Changed(object sender, RoutedEventArgs e)
    {
        if (_refreshingDiagnostics) return;
        var enabled = DetailedLiteraryDiagnosticsCheckBox.IsChecked == true;
        _appSettings.DetailedLiteraryDiagnostics = enabled;
        _appSettingsStore.Save(_appSettings);
        LiteraryRequestDiagnostics.Enabled = enabled;
    }
    private void OpenLiteraryDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(LiteraryRequestDiagnostics.Root);
            Process.Start(new ProcessStartInfo(LiteraryRequestDiagnostics.Root) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { System.Windows.MessageBox.Show(this, L("Diagnostics.Literary.OpenFailed"), L("Settings.Title")); }
    }
}
