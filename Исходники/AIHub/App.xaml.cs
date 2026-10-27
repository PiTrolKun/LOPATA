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
    public static bool VectorFileLaunch { get; private set; }
    public static bool VideoFileLaunch { get; private set; }
    protected override void OnStartup(StartupEventArgs e)
    {
        if(e.Args is ["--dictation-worker"]) {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            _=RunDictationWorkerAsync();return;
        }
        if (e.Args is ["--remove-book-shell"])
        {
            try { new Services.Books.BookShellIntegration(Environment.ProcessPath!).Apply(false, _ => ""); Shutdown(); }
            catch { Shutdown(1); }
            return;
        }
        if (e.Args is ["--remove-vector-shell"])
        {
            try { new Services.VectorEditor.VectorShellIntegration(Environment.ProcessPath!).Apply(false,""); Shutdown(); }
            catch { Shutdown(1); }
            return;
        }
        if (e.Args is ["--remove-video-shell"])
        {
            try { Services.VideoEditor.VideoShellIntegration.RemoveOwnedProjects(Environment.ProcessPath!); Shutdown(); }
            catch { Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--shell-book", StringComparer.Ordinal))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            // No MainWindow, single-instance activation, dialogs, or tray are created for this verb.
            _ = RunBookShellAsync(e.Args);
            return;
        }
        if (e.Args is ["--owned-console-stop", var pidText, var ticksText]
            && int.TryParse(pidText, out var pid) && long.TryParse(ticksText, out var ticks))
        {
            Shutdown(AIHub.Services.WindowsConsoleProcess.Signal(pid, ticks));
            return;
        }
        ImageShellLaunch = e.Args.Contains("--shell-image", StringComparer.Ordinal);
        AudioShellLaunch = e.Args.Contains("--shell-audio", StringComparer.Ordinal);
        VectorFileLaunch = e.Args.Contains("--svg", StringComparer.Ordinal);
        VideoFileLaunch = e.Args.Contains("--video-project", StringComparer.Ordinal);
        Models.ImageShellRequest? imageRequest;
        Models.AudioShellRequest? audioRequest;
        Services.VectorEditor.VectorFileRequest? vectorRequest;
        Services.VideoEditor.VideoFileRequest? videoRequest;
        try
        {
            Services.OwnedProcessRegistry.Log("startup_enter", "Application", Environment.ProcessId);
            imageRequest = Models.ImageShellRequest.ParseArguments(e.Args);
            audioRequest = Models.AudioShellRequest.ParseArguments(e.Args);
            vectorRequest = Services.VectorEditor.VectorFileRequest.Parse(e.Args);
            videoRequest = Services.VideoEditor.VideoFileRequest.Parse(e.Args);
            if (new[] {ImageShellLaunch,AudioShellLaunch,VectorFileLaunch,VideoFileLaunch}.Count(v=>v)>1) throw new System.IO.InvalidDataException("Mixed shell requests.");
            BackgroundLaunch = ImageShellLaunch || AudioShellLaunch || VectorFileLaunch || VideoFileLaunch || e.Args.Contains("--background", StringComparer.Ordinal);
            _instance = Services.SingleApplicationInstance.Acquire(BackgroundLaunch, imageRequest, audioRequest, vectorRequest, videoRequest);
            if (_instance is null) { Shutdown(); return; }
            if (Services.ApplicationUpdateStartup.RedirectInstalledLaunch(e.Args)) { Shutdown(); return; }
        }
        catch (Exception error)
        {
            if (ImageShellLaunch || AudioShellLaunch || VectorFileLaunch || VideoFileLaunch)
            {
                Services.OwnedProcessRegistry.Log(VideoFileLaunch ? "video_file_launch_failed" : AudioShellLaunch ? "audio_shell_launch_failed" : "image_shell_launch_failed", "Application", detail: error.GetType().Name);
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
            Services.OwnedProcessRegistry.Log("startup_window_construct_begin", "Application", Environment.ProcessId);
            var window = new MainWindow(); MainWindow = window;
            Services.OwnedProcessRegistry.Log("startup_window_construct_end", "Application", Environment.ProcessId);
            _instance.OpenRequested += () => Dispatcher.BeginInvoke(window.OpenFromSecondInstance);
            if (ImageShellLaunch || AudioShellLaunch || VectorFileLaunch || VideoFileLaunch || (BackgroundLaunch && window.CanStartInTray))
            {
                Services.ApplicationBackgroundOperations.PreserveHiddenWork = true;
                new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
            }
            else window.Show();
            Services.OwnedProcessRegistry.Log(window.IsVisible ? "startup_window_visible" : "startup_window_background", "Application", Environment.ProcessId);
            if (!VectorFileLaunch && !VideoFileLaunch) window.CompleteApplicationStartup();
            if (videoRequest is not null) window.AcceptVideoFile(videoRequest);
            _instance.SetVideoHandler(request => Dispatcher.InvokeAsync(() => window.AcceptVideoFile(request)).Task);
            if (vectorRequest is not null) _ = window.AcceptVectorFileAsync(vectorRequest);
            _instance.SetVectorHandler(request => Dispatcher.InvokeAsync(() => window.AcceptVectorFileAsync(request)).Task.Unwrap());
            if (imageRequest is not null) window.AcceptImageShellRequest(imageRequest);
            _instance.SetImageShellHandler(request => Dispatcher.InvokeAsync(() => window.AcceptImageShellRequest(request)).Task);
            if (audioRequest is not null) window.AcceptAudioShellRequest(audioRequest);
            _instance.SetAudioShellHandler(request => Dispatcher.InvokeAsync(() => window.AcceptAudioShellRequest(request)).Task);
            _instance.DeliverPendingOpen();
        }
        catch (Exception error) when (ImageShellLaunch || AudioShellLaunch || VectorFileLaunch || VideoFileLaunch)
        {
            Services.OwnedProcessRegistry.Log(VideoFileLaunch ? "video_file_startup_failed" : AudioShellLaunch ? "audio_shell_startup_failed" : "image_shell_startup_failed", "Application", detail: error.GetType().Name);
            Shutdown(1);
        }
    }

    private async Task RunBookShellAsync(string[] args)
    {
        int code;
        try { code = await Services.Books.BookShellRunner.RunAsync(args); }
        catch (Exception error)
        {
            Services.OwnedProcessRegistry.Log("book_shell_startup_failed", "Application", detail: error.GetType().Name);
            code = 1;
        }
        Shutdown(code);
    }

    private async Task RunDictationWorkerAsync() => Shutdown(await Services.Dictation.DictationWorker.RunAsync());

    protected override void OnExit(ExitEventArgs e)
    {
        AIHub.Services.OwnedProcessRegistry.Shared.Dispose();
        _instance?.Dispose(); _instance = null;
        base.OnExit(e);
    }
}

