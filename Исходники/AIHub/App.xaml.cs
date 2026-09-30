using System.Configuration;
using System.Data;
using System.Windows;

namespace AIHub;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    private Services.SingleApplicationInstance? _instance;
    public static bool BackgroundLaunch { get; private set; }
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
            BackgroundLaunch = e.Args.Contains("--background", StringComparer.Ordinal);
            _instance = Services.SingleApplicationInstance.Acquire(BackgroundLaunch);
            if (_instance is null) { Shutdown(); return; }
            if (Services.ApplicationUpdateStartup.RedirectInstalledLaunch(e.Args)) { Shutdown(); return; }
        }
        catch (Exception)
        {
            var localization = new Services.LocalizationService();
            localization.Load(Services.LocalizationService.GetWindowsLanguageCode());
            System.Windows.MessageBox.Show(localization.T("Updates.LaunchFailed"), localization.T("Updates.Title"), MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1); return;
        }
        base.OnStartup(e);
        var window = new MainWindow(); MainWindow = window;
        _instance.OpenRequested += () => Dispatcher.BeginInvoke(window.OpenFromSecondInstance);
        if (BackgroundLaunch && window.CanStartInTray)
        {
            Services.ApplicationBackgroundOperations.PreserveHiddenWork = true;
            new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
        }
        else window.Show();
        window.CompleteApplicationStartup();
        _instance.DeliverPendingOpen();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AIHub.Services.OwnedProcessRegistry.Shared.Dispose();
        _instance?.Dispose(); _instance = null;
        base.OnExit(e);
    }
}

