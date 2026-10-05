using System.Windows.Threading;
using AIHub.Models;
using AIHub.Services;

namespace AIHub;

public partial class MainWindow
{
    private BackgroundOperationController? _backgroundOperations;
    private readonly BackgroundResumeCountdown _backgroundCountdown = new();
    private readonly DispatcherTimer _backgroundTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CancellationTokenSource _backgroundLifetime = new();
    private bool _backgroundCommandRunning;
    private bool _applicationReady;
    internal bool CanStartInTray => _applicationTray?.IsAvailable == true;
    internal void OpenFromSecondInstance() => RestoreFromTray();

    private void InitializeBackgroundOperations()
    {
        _backgroundOperations = new(new(System.IO.Path.Combine(AppDataPaths.BaseDirectory, "Background", "operation.json")));
        ApplicationBackgroundOperations.Current = _backgroundOperations;
        ApplicationBackgroundOperations.ExitToken = _backgroundLifetime.Token;
        _backgroundOperations.Register(Controls.LiteraryStudioControl.BackgroundKind,
            (state, token) => LiteraryPage.ResumeBackgroundStudioAsync(state, token));
        _backgroundOperations.Register(Controls.LiteraryWorkspaceControl.MemoryBackgroundKind,
            (state, token) => LiteraryPage.ResumeBackgroundMemoryAsync(state, token));
        _backgroundOperations.Register(Controls.LiteraryImportControl.BackgroundKind,
            (state, token) => LiteraryPage.ResumeBackgroundImportAsync(state, token));
        RegisterImageBackgroundOperations();
        RegisterSandboxBackgroundOperations();
        RegisterFinancialBackgroundOperation();
        RegisterImageGenerationBackgroundOperation();
        RegisterImageUtilityBackgroundOperation();
        RegisterImageShellBackgroundOperation();
        _backgroundOperations.Register(MusicGenerationRunner.BackgroundKind, (state, token) =>
        { ConfigureMusic(); return MusicPage.ResumeGenerationAsync(state, token); });
        _backgroundOperations.Changed += () =>
        { if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(RefreshBackgroundTray); };
        _backgroundOperations.Completed += notice => Dispatcher.BeginInvoke(() =>
        {
            System.Media.SystemSounds.Asterisk.Play();
            _applicationTray?.Notify(L("Tray.ResultReady"), notice.Title, onOpen: ViewBackgroundResult);
            RefreshBackgroundTray();
        });
        try { _backgroundOperations.Load(); }
        catch (Exception error)
        {
            OwnedProcessRegistry.Log("background_restore_failed", "Application", detail: error.GetType().Name);
            StatusText.Text = L("Tray.RestoreFailed");
        }
        _backgroundTimer.Tick += async (_, _) =>
        {
            RefreshBackgroundTray();
            if (_backgroundCountdown.IsActive && _backgroundCountdown.Remaining(DateTimeOffset.UtcNow) == 0)
            {
                _backgroundCountdown.Cancel(); _backgroundOperations.SetCountdown(false);
                await ResumeBackgroundOperationAsync();
            }
        };
        Closed += (_, _) =>
        {
            _backgroundTimer.Stop(); _backgroundLifetime.Cancel();
            ApplicationBackgroundOperations.Current = null;
        };
        _backgroundTimer.Start();
    }

    internal async void CompleteApplicationStartup()
    {
        _stateBeforeTray = WindowState == System.Windows.WindowState.Minimized ? _lastNonMinimizedWindowState : WindowState;
        _applicationReady = true;
        ApplyImageShellIntegration();
        RefreshBackgroundResumeSchedule(); RefreshBackgroundTray();
        await CompleteUpdateStartupAsync();
        await PumpImageShellAsync();
    }

    private void RefreshBackgroundResumeSchedule()
    {
        if (!_applicationReady || _backgroundOperations is null) return;
        var state = _backgroundOperations.State;
        if (_appSettings.Behavior.AutoResumeBackgroundOperation && !_backgroundOperations.IsRunning
            && _backgroundOperations.CanRestore && state is { UserPaused: false, RequiresDecision: false, Phase: BackgroundOperationPhase.Waiting })
        { _backgroundCountdown.Start(DateTimeOffset.UtcNow); _backgroundOperations.SetCountdown(true); }
        else if (!_appSettings.Behavior.AutoResumeBackgroundOperation)
        { _backgroundCountdown.Cancel(); _backgroundOperations.SetCountdown(false); }
        RefreshBackgroundTray();
    }

    private void RefreshBackgroundTray()
    {
        var state = _backgroundOperations?.State;
        _applicationTray?.SetOperation(_backgroundOperations?.HasPending == true ? state?.Phase : null,
            _backgroundCountdown.IsActive ? _backgroundCountdown.Remaining(DateTimeOffset.UtcNow) : null);
        _applicationTray?.SetAttention(state?.NeedsAttention == true, ApplicationUpdateNotice.Visibility == System.Windows.Visibility.Visible || _availableUpdate is not null);
    }

    private async Task ChangeBackgroundOperationAsync()
    {
        if (_backgroundCommandRunning || _backgroundOperations is null) return;
        _backgroundCommandRunning = true;
        try
        {
            if (_backgroundCountdown.IsActive || _backgroundOperations.State?.Phase is BackgroundOperationPhase.Running or BackgroundOperationPhase.Pausing)
            { _backgroundCountdown.Cancel(); await _backgroundOperations.PauseAsync(); }
            else await ResumeBackgroundOperationAsync();
        }
        catch (Exception error) { ReportBackgroundFailure(error); }
        finally { _backgroundCommandRunning = false; RefreshBackgroundTray(); }
    }

    private async Task ResumeBackgroundOperationAsync()
    {
        try
        {
            if (_backgroundOperations is null) return;
            var state = _backgroundOperations.State;
            if (state is { RequiresDecision: true } && state.Kind is ImageBackgroundWork.Kind or ImageBatchBackgroundKind
                or ImageSpeechBackgroundKind or ImagePreparationBackgroundKind or SandboxCoreBackgroundKind or ExecutorWorkflowService.BackgroundKind)
                RestoreFromTray();
            if (state is { RequiresDecision: true, Project: not null, Kind: Controls.LiteraryImportControl.BackgroundKind })
                RevealLiteraryBackgroundPage(() => LiteraryPage.RestoreImportPage(state.Project));
            if (state is { RequiresDecision: true, Project: not null }
                && state.Kind is Controls.LiteraryStudioControl.BackgroundKind or Controls.LiteraryWorkspaceControl.MemoryBackgroundKind)
                RevealLiteraryBackgroundWorkspace(state.Project);
            if (state is { RequiresDecision: true, Project: not null, Kind: FinancialAnalysisPlan.BackgroundKind })
            {
                ViewFinancialBackgroundResult(new(state.Id, state.Kind, state.Title, state.Project, true));
                return; // Let the user choose a model or review the private files before retrying.
            }
            await _backgroundOperations.ResumeAsync(_backgroundLifetime.Token);
        }
        catch (OperationCanceledException) when (_backgroundLifetime.IsCancellationRequested) { }
        catch (Exception error) { ReportBackgroundFailure(error); }
    }

    private void ReportBackgroundFailure(Exception error)
    {
        OwnedProcessRegistry.Log("background_command_failed", "Application", detail: error.GetType().Name);
        StatusText.Text = L(error is BackgroundOperationWaitingException waiting ? waiting.Message : "Tray.OperationFailed");
        _applicationTray?.Notify(L("Settings.Title"), StatusText.Text, warning: true);
    }

    private async void ViewBackgroundResult()
    {
        var notice = _backgroundOperations?.State?.Notice;
        if (notice is null) return;
        try
        {
            if (notice.Kind == MusicGenerationRunner.BackgroundKind && notice.Project is not null)
            {
                RestoreFromTray(); OpenMusicScenario(false); MusicPage.ViewGenerationResult(notice.Project);
                _backgroundOperations!.Acknowledge(notice.Id); return;
            }
            if (notice.Kind == FinancialAnalysisPlan.BackgroundKind)
            {
                if (ViewFinancialBackgroundResult(notice)) _backgroundOperations!.Acknowledge(notice.Id);
                return;
            }
            if (notice.Kind == ImageGenerationCatalog.BackgroundKind && notice.Project is not null)
            {
                RestoreFromTray(); ShowBackgroundScenarioPage(WorkStartPage);
                OpenImageGenerationScenario(); GenerationPage.Restore(notice.Project);
                _backgroundOperations!.Acknowledge(notice.Id);
                return;
            }
            if (notice.Kind == ImageShellQueueRunner.BackgroundKind && notice.Project is not null)
            {
                ViewImageShellResult(notice.Project);
                _backgroundOperations!.Acknowledge(notice.Id);
                return;
            }
            if (notice.Kind == ImageUtilityQueue.BackgroundKind && notice.Project is not null)
            {
                RestoreFromTray(); ShowBackgroundScenarioPage(WorkStartPage);
                OpenImageUtilityScenario(); ImageUtilityPage.Restore(notice.Project);
                _backgroundOperations!.Acknowledge(notice.Id);
                return;
            }
            if (notice.Kind is ImagePromptAssistant.BackgroundKind or ImageReferenceAnalyzer.BackgroundKind)
            {
                RestoreFromTray(); ShowBackgroundScenarioPage(WorkStartPage);
                OpenImageGenerationScenario();
                if (notice.Kind == ImageReferenceAnalyzer.BackgroundKind) GenerationPage.RestoreReferenceResult(notice);
                else GenerationPage.RestorePromptResult(notice);
                _backgroundOperations!.Acknowledge(notice.Id);
                return;
            }
            if (notice.Kind is ImageBackgroundWork.Kind or ImageBatchBackgroundKind or ImageSpeechBackgroundKind or ImagePreparationBackgroundKind)
            {
                if (ViewBackgroundImageResult(notice)) _backgroundOperations!.Acknowledge(notice.Id);
                return;
            }
            if (notice.Kind is SandboxCoreBackgroundKind or ExecutorWorkflowService.BackgroundKind)
            {
                if (await ViewBackgroundSandboxResultAsync(notice)) _backgroundOperations!.Acknowledge(notice.Id);
                return;
            }
            if (notice.Kind == Controls.LiteraryImportControl.BackgroundKind && notice.Project is not null)
            {
                RevealLiteraryBackgroundPage(() => LiteraryPage.RestoreImportPage(notice.Project));
                if (await LiteraryPage.ViewBackgroundImportResultAsync(notice, _backgroundLifetime.Token))
                    _backgroundOperations!.Acknowledge(notice.Id);
                return;
            }
            if (notice.Project is not null && notice.Kind is Controls.LiteraryStudioControl.BackgroundKind or Controls.LiteraryWorkspaceControl.MemoryBackgroundKind)
            {
                RevealLiteraryBackgroundWorkspace(notice.Project);
                if (LiteraryPage.ViewBackgroundResult(notice)) _backgroundOperations!.Acknowledge(notice.Id);
            }
        }
        catch (Exception error) { ReportBackgroundFailure(error); }
    }

    private void RevealLiteraryBackgroundWorkspace(string project)
    {
        RevealLiteraryBackgroundPage(() => LiteraryPage.RestoreWorkspace(project));
    }

    private void RevealLiteraryBackgroundPage(Action restore)
    {
        restore();
        RestoreFromTray();
        ApplicationBackgroundOperations.PreserveHiddenWork = true;
        foreach (var page in new System.Windows.FrameworkElement[] { WelcomePage, SettingsPage, SetupPage, ProfilePage,
            ProfileReminderPage, WorkStartPage, ChoiceScenarioPage, ImageAnalysisWorkspacePage,
            ImageAnalysisBundleConfirmationPage, ImageAnalysisBundleSelectorPage }) page.Visibility = System.Windows.Visibility.Collapsed;
        LiteraryPage.Visibility = System.Windows.Visibility.Visible;
        ApplicationBackgroundOperations.PreserveHiddenWork = false;
    }
}
