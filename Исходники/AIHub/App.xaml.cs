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
    public static bool ImageShellLaunch { get; private set; }
    public static bool AudioShellLaunch { get; private set; }
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args is ["--owned-console-stop", var pidText, var ticksText]
            && int.TryParse(pidText, out var pid) && long.TryParse(ticksText, out var ticks))
        {
            Shutdown(AIHub.Services.WindowsConsoleProcess.Signal(pid, ticks));
            return;
        }
        ImageShellLaunch = e.Args.Contains("--shell-image", StringComparer.Ordinal);
        AudioShellLaunch = e.Args.Contains("--shell-audio", StringComparer.Ordinal);
        Models.ImageShellRequest? imageRequest;
        Models.AudioShellRequest? audioRequest;
        try
        {
            imageRequest = Models.ImageShellRequest.ParseArguments(e.Args);
            audioRequest = Models.AudioShellRequest.ParseArguments(e.Args);
            if (ImageShellLaunch && AudioShellLaunch) throw new System.IO.InvalidDataException("Mixed shell requests.");
            BackgroundLaunch = ImageShellLaunch || AudioShellLaunch || e.Args.Contains("--background", StringComparer.Ordinal);
            _instance = Services.SingleApplicationInstance.Acquire(BackgroundLaunch, imageRequest, audioRequest);
            if (_instance is null) { Shutdown(); return; }
            if (Services.ApplicationUpdateStartup.RedirectInstalledLaunch(e.Args)) { Shutdown(); return; }
        }
        catch (Exception error)
        {
            if (ImageShellLaunch || AudioShellLaunch)
            {
                Services.OwnedProcessRegistry.Log(AudioShellLaunch ? "audio_shell_launch_failed" : "image_shell_launch_failed", "Application", detail: error.GetType().Name);
                Shutdown(1); return;
            }
            var localization = new Services.LocalizationService();
            localization.Load(Services.LocalizationService.GetWindowsLanguageCode());
            System.Windows.MessageBox.Show(localization.T("Updates.LaunchFailed"), localization.T("Updates.Title"), MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1); return;
        }
        try
        {
            base.OnStartup(e);
            var window = new MainWindow(); MainWindow = window;
            _instance.OpenRequested += () => Dispatcher.BeginInvoke(window.OpenFromSecondInstance);
            if (ImageShellLaunch || AudioShellLaunch || (BackgroundLaunch && window.CanStartInTray))
            {
                Services.ApplicationBackgroundOperations.PreserveHiddenWork = true;
                new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
            }
            else window.Show();
            window.CompleteApplicationStartup();
            if (imageRequest is not null) window.AcceptImageShellRequest(imageRequest);
            _instance.SetImageShellHandler(request => Dispatcher.InvokeAsync(() => window.AcceptImageShellRequest(request)).Task);
            if (audioRequest is not null) window.AcceptAudioShellRequest(audioRequest);
            _instance.SetAudioShellHandler(request => Dispatcher.InvokeAsync(() => window.AcceptAudioShellRequest(request)).Task);
            _instance.DeliverPendingOpen();
        }
        catch (Exception error) when (ImageShellLaunch || AudioShellLaunch)
        {
            Services.OwnedProcessRegistry.Log(AudioShellLaunch ? "audio_shell_startup_failed" : "image_shell_startup_failed", "Application", detail: error.GetType().Name);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AIHub.Services.OwnedProcessRegistry.Shared.Dispose();
        _instance?.Dispose(); _instance = null;
        base.OnExit(e);
    }
}

