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
    private Services.IslandHostServer? _islandServer;
    private CancellationTokenSource? _mailHubLifetime;
    private Mutex? _mailHubProcess;
    public static bool BackgroundLaunch { get; private set; }
    public static bool ImageShellLaunch { get; private set; }
    public static bool AudioShellLaunch { get; private set; }
    public static bool VectorFileLaunch { get; private set; }
    public static bool VideoFileLaunch { get; private set; }
    public static bool ReaderFileLaunch { get; private set; }
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args is ["--mail-hub-island", var mailPid, var mailStart]
            && int.TryParse(mailPid, out var ownerId) && long.TryParse(mailStart, out var ownerStart))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunIslandMailHubAsync(ownerId, ownerStart);
            return;
        }
        if (e.Args is ["--island-core", var islandCorePipe])
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunIslandCoreAsync(islandCorePipe);
            return;
        }
        if (e.Args is ["--initialize-collection"])
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = InitializeCollectionAsync();
            return;
        }
        if(e.Args is ["--dictation-worker"]) {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            _=RunDictationWorkerAsync();return;
        }
        if (e.Args is ["--island-music-voice"])
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunIslandMusicVoiceAsync();
            return;
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
        if (e.Args is ["--remove-reader-shell"])
        {
            try { new Services.OfficeReader.ReaderShellIntegration(Environment.ProcessPath!).Apply(false, "", ""); Shutdown(); }
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
        SafeCrashRecoveryLaunch = e.Args.Contains("--recover-safe", StringComparer.Ordinal);
        var recovering = SafeCrashRecoveryLaunch || e.Args.Contains("--recover-session", StringComparer.Ordinal);
        var args = e.Args.Where(argument => argument is not ("--recover-session" or "--recover-safe")).ToArray();
        ImageShellLaunch = args.Contains("--shell-image", StringComparer.Ordinal);
        AudioShellLaunch = args.Contains("--shell-audio", StringComparer.Ordinal);
        VectorFileLaunch = args.Contains("--svg", StringComparer.Ordinal);
        VideoFileLaunch = args.Contains("--video-project", StringComparer.Ordinal);
        ReaderFileLaunch = args.Contains("--reader", StringComparer.Ordinal)
            || (args is [var fileArgument] && !fileArgument.StartsWith("--", StringComparison.Ordinal));
        Models.ImageShellRequest? imageRequest;
        Models.AudioShellRequest? audioRequest;
        Services.VectorEditor.VectorFileRequest? vectorRequest;
        Services.VideoEditor.VideoFileRequest? videoRequest;
        Services.OfficeReader.ReaderFileRequest? readerRequest;
        try
        {
            Services.OwnedProcessRegistry.Log("startup_enter", "Application", Environment.ProcessId);
            imageRequest = Models.ImageShellRequest.ParseArguments(args);
            audioRequest = Models.AudioShellRequest.ParseArguments(args);
            vectorRequest = Services.VectorEditor.VectorFileRequest.Parse(args);
            videoRequest = Services.VideoEditor.VideoFileRequest.Parse(args);
            readerRequest = Services.OfficeReader.ReaderFileRequest.Parse(args);
            if (new[] {ImageShellLaunch,AudioShellLaunch,VectorFileLaunch,VideoFileLaunch,ReaderFileLaunch}.Count(v=>v)>1) throw new System.IO.InvalidDataException("Mixed shell requests.");
            BackgroundLaunch = ImageShellLaunch || AudioShellLaunch || VectorFileLaunch || VideoFileLaunch || ReaderFileLaunch || args.Contains("--background", StringComparer.Ordinal);
            _instance = Services.SingleApplicationInstance.Acquire(BackgroundLaunch, imageRequest, audioRequest, vectorRequest, videoRequest, readerRequest);
            if (_instance is null) { Shutdown(); return; }
            if (Services.ApplicationUpdateStartup.RedirectInstalledLaunch(e.Args))
            {
                NotifyRecoveryLaunchRedirect();
                Shutdown();
                return;
            }
        }
        catch (Exception error)
        {
            if (ImageShellLaunch || AudioShellLaunch || VectorFileLaunch || VideoFileLaunch || ReaderFileLaunch)
            {
                Services.OwnedProcessRegistry.Log(ReaderFileLaunch ? "reader_file_launch_failed" : VideoFileLaunch ? "video_file_launch_failed" : AudioShellLaunch ? "audio_shell_launch_failed" : "image_shell_launch_failed", "Application", detail: error.GetType().Name);
                Shutdown(1); return;
            }
            var localization = new Services.LocalizationService();
            localization.Load(Services.LocalizationService.GetWindowsLanguageCode());
            System.Windows.MessageBox.Show(localization.T("Updates.LaunchFailed"), localization.T("Updates.Title"), MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1); return;
        }
        try
        {
            if (Services.ApplicationUpdateStartup.HealthTransactionId is null) StartRecoveryMonitoring(recovering);
            else Services.ApplicationUpdateStartup.PrepareVoiceSettingsRecovery();
            base.OnStartup(e);
            if (System.IO.File.Exists(Services.ModelStorageAddresses.Current.JournalPath))
                try
                {
                    using var relocation = Services.ModelStorageWorkGate.Current.TryBeginTransfer();
                    if (relocation is not null)
                        new Services.ModelStorageTransfer(Services.ModelStorageAddresses.Current).TryRecover(leaveCommittedForLater: true);
                }
                catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
                { Services.OwnedProcessRegistry.Log("model_transfer_recovery_pending", "ModelStorage", detail: error.GetType().Name); }
            Services.OwnedProcessRegistry.Log("startup_window_construct_begin", "Application", Environment.ProcessId);
            var window = new MainWindow(); MainWindow = window;
            window.InitializeCrashWorkspaceRecovery(recovering && !SafeCrashRecoveryLaunch);
            Services.OwnedProcessRegistry.Log("startup_window_construct_end", "Application", Environment.ProcessId);
            _instance.OpenRequested += () => Dispatcher.BeginInvoke(window.OpenFromSecondInstance);
            var startInTray = !SafeCrashRecoveryLaunch && (window.CrashWorkspaceStartsInTray ?? BackgroundLaunch);
            if (ImageShellLaunch || AudioShellLaunch || VectorFileLaunch || VideoFileLaunch || ReaderFileLaunch || (startInTray && window.CanStartInTray))
            {
                Services.ApplicationBackgroundOperations.PreserveHiddenWork = true;
                new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
            }
            else window.Show();
            Services.OwnedProcessRegistry.Log(window.IsVisible ? "startup_window_visible" : "startup_window_background", "Application", Environment.ProcessId);
            if (!VectorFileLaunch && !VideoFileLaunch && !ReaderFileLaunch) window.CompleteApplicationStartup();
            if (readerRequest is not null) { window.ApplyReaderIntegration(); window.AcceptReaderFile(readerRequest); }
            _instance.SetReaderHandler(request => Dispatcher.InvokeAsync(() => window.AcceptReaderFile(request)).Task);
            if (videoRequest is not null) window.AcceptVideoFile(videoRequest);
            _instance.SetVideoHandler(request => Dispatcher.InvokeAsync(() => window.AcceptVideoFile(request)).Task);
            if (vectorRequest is not null) _ = window.AcceptVectorFileAsync(vectorRequest);
            _instance.SetVectorHandler(request => Dispatcher.InvokeAsync(() => window.AcceptVectorFileAsync(request)).Task.Unwrap());
            if (imageRequest is not null) window.AcceptImageShellRequest(imageRequest);
            _instance.SetImageShellHandler(request => Dispatcher.InvokeAsync(() => window.AcceptImageShellRequest(request)).Task);
            if (audioRequest is not null) window.AcceptAudioShellRequest(audioRequest);
            _instance.SetAudioShellHandler(request => Dispatcher.InvokeAsync(() => window.AcceptAudioShellRequest(request)).Task);
            _islandServer = new((request, token) =>
                Dispatcher.InvokeAsync(() => window.HandleIslandRequestAsync(request, token),
                    System.Windows.Threading.DispatcherPriority.Normal, token).Task.Unwrap());
            StartMailHub(window);
            _instance.DeliverPendingOpen();
        }
        catch (Exception error) when (ImageShellLaunch || AudioShellLaunch || VectorFileLaunch || VideoFileLaunch || ReaderFileLaunch)
        {
            Services.OwnedProcessRegistry.Log(ReaderFileLaunch ? "reader_file_startup_failed" : VideoFileLaunch ? "video_file_startup_failed" : AudioShellLaunch ? "audio_shell_startup_failed" : "image_shell_startup_failed", "Application", detail: error.GetType().Name);
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
    private async Task RunIslandMusicVoiceAsync() =>
        Shutdown(await Task.Run(Services.IslandMusicVoiceWorker.RunAsync));
    private async Task RunIslandCoreAsync(string pipe) => Shutdown(await Services.IslandCoreWorker.RunAsync(pipe));

    private async Task RunIslandMailHubAsync(int pid, long ticks)
    {
        try { Shutdown(await Task.Run(() => Services.MailHub.MailHubScheduler.RunIslandAsync(pid, ticks))); }
        catch (Exception error)
        {
            Services.OwnedProcessRegistry.Log("mail_hub_worker_failed", "MailHub", detail: error.GetType().Name);
            Shutdown(2);
        }
    }

    private void StartMailHub(MainWindow window)
    {
        var files = new Lopata.MailHub.MailHubFiles(Services.AppDataPaths.BaseDirectory);
        _mailHubProcess = files.ProcessMarker(island: false);
        var lifetime = _mailHubLifetime = new();
        var scheduler = new Services.MailHub.MailHubScheduler(files, headless: false,
            () => Dispatcher.InvokeAsync(window.IsMailHubChatBusy).Task,
            (status, details) => Dispatcher.BeginInvoke(() => window.ShowMailHubNotice(status, details)));
        _ = Task.Run(() => scheduler.RunAsync(lifetime.Token));
    }

    private async Task InitializeCollectionAsync()
    {
        var exitCode = 0;
        try { await Services.CollectionInitializationService.InitializeCurrentAsync(); }
        catch (Exception error)
        {
            Services.OwnedProcessRegistry.Log("collection_initialization_failed", "Collection", detail: error.GetType().Name);
            var message = error.GetType().Name + ": " + error.Message;
            Console.Error.WriteLine(message[..Math.Min(message.Length, 2048)]);
            exitCode = 1;
        }
        Shutdown(exitCode);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mailHubLifetime?.Cancel();
        _mailHubProcess?.Dispose();
        _islandServer?.Dispose();
        AIHub.Services.OwnedProcessRegistry.Shared.Dispose();
        _instance?.Dispose(); _instance = null;
        base.OnExit(e);
        CompleteRecoveryExit(e.ApplicationExitCode);
    }
}
