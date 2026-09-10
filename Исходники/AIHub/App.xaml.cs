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
        StartupUri = new Uri("MainWindow.xaml", UriKind.Relative);
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AIHub.Services.OwnedProcessRegistry.Shared.Dispose();
        base.OnExit(e);
    }
}

