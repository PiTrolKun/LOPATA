using System.Configuration;
using System.Data;
using System.Windows;

namespace AIHub;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args is ["--owned-console-stop", var pidText, var ticksText]
            && int.TryParse(pidText, out var pid) && long.TryParse(ticksText, out var ticks))
        {
            Shutdown(AIHub.Services.WindowsConsoleProcess.Signal(pid, ticks));
            return;
        }
        try
        {
            if (Services.ApplicationUpdateStartup.RedirectInstalledLaunch(e.Args)) { Shutdown(); return; }
        }
        catch (Exception)
        {
            var localization = new Services.LocalizationService();
            localization.Load(Services.LocalizationService.GetWindowsLanguageCode());
            System.Windows.MessageBox.Show(localization.T("Updates.LaunchFailed"), localization.T("Updates.Title"), MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1); return;
        }
        StartupUri = new Uri("MainWindow.xaml", UriKind.Relative);
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AIHub.Services.OwnedProcessRegistry.Shared.Dispose();
        base.OnExit(e);
    }
}

