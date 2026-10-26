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
    internal void OpenFromSecondInstance() { CompleteApplicationStartup(); RestoreFromTray(); }

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
        RegisterVectorBackgroundOperation();
        RegisterImageUtilityBackgroundOperation();
        RegisterRagBookBackgroundOperation();
        RegisterImageShellBackgroundOperation();
        RegisterLyricVideoBackgroundOperation();
        RegisterVideoEditorOperations();
        RegisterBookSmartBackgroundOperation();
        RegisterLyricLibraryBackgroundOperation();
        _backgroundOperations.Register(MusicGenerationRunner.BackgroundKind, (state, token) =>
        { ConfigureMusic(); return MusicPage.ResumeGenerationAsync(state, token); });
        _backgroundOperations.Register(Services.MusicScore.MusicScorePlanWork.Kind,(state,token)=>
        {ConfigureMusic();return MusicPage.ResumeScoreAsync(state,token);});
        _backgroundOperations.Register(Services.MusicAceIdeas.MusicAceIdeaWork.Kind,(state,token)=>
        {ConfigureMusic();return MusicPage.ResumeAceIdeasAsync(state,token);});
        _backgroundOperations.Register(MusicLyricsSynchronization.BackgroundKind, (state, token) =>
        { ConfigureMusic(); return MusicPage.ResumeLyricsSyncAsync(state, token); });
        _backgroundOperations.Register(MusicRhymeWork.Kind, (state, token) =>
        { ConfigureMusic(); return MusicPage.ResumeRhymeAsync(state, token); });
        _backgroundOperations.Register(MusicCoverWork.Kind, (state, token) =>
        { ConfigureMusic(); return MusicPage.ResumeCoverAsync(state, token); });
        _backgroundOperations.Changed += () =>
        { if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(RefreshBackgroundTray); };
        _backgroundOperations.Completed += notice => Dispatcher.BeginInvoke(() =>
        {
            System.Media.SystemSounds.Asterisk.Play();
            _applicationTray?.Notify(L("Tray.ResultReady"), notice.Title, onOpen: ViewBackgroundResult);
            RefreshBackgroundTray();
        });
        try {
            _backgroundOperations.Load(); ImageGenerationFailureRecovery.Recover(_backgroundOperations);
            Services.VectorGraphics.VectorWork.RecoverFailedContent(_backgroundOperations);
            MusicDiffRhythmFailureRecovery.Recover(_backgroundOperations, MusicGenerationJobs.Default);
        }
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

    private Task? _applicationStartupTask;
    internal void CompleteApplicationStartup() => _ = EnsureApplicationStartupAsync();
    internal Task EnsureApplicationStartupAsync() => _applicationStartupTask ??= CompleteApplicationStartupCoreAsync();
    private async Task CompleteApplicationStartupCoreAsync()
    {
        try
        {
            _stateBeforeTray = WindowState == System.Windows.WindowState.Minimized ? _lastNonMinimizedWindowState : WindowState;
            try { await InitializeEngineSetsAsync(); }
            catch (OperationCanceledException) { return; }
            catch (Exception error) { EngineSetFailure(error); return; }
            await InitializeStartupChecksAsync();
            if (_backgroundLifetime.IsCancellationRequested || _processShutdownPending || _processShutdownComplete) return;
            _applicationReady = true;
            PrewarmDictationAfterStartup();
            ApplyImageShellIntegration();
            RefreshBackgroundResumeSchedule(); RefreshBackgroundTray();
            await CompleteUpdateStartupAsync();
            try { await OfferEngineSetAsync(); }
            catch (OperationCanceledException) { return; }
            catch (Exception error) { EngineSetFailure(error); }
            await PumpImageShellAsync();
        }
        finally
        {
            if (!Dispatcher.HasShutdownStarted && !_processShutdownPending && !_processShutdownComplete)
                _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(AnnounceComponentAuditStartup));
        }
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
        _applicationTray?.SetPausedOperations(_backgroundOperations?.PausedOperations ?? [], _backgroundOperations?.BlocksNewWork == true,
            id => Dispatcher.BeginInvoke(() => ResumePausedOperation(id)));
        _applicationTray?.SetAttention(state?.NeedsAttention == true, ApplicationUpdateNotice.Visibility == System.Windows.Visibility.Visible || _availableUpdate is not null);
        var books = state?.Kind is RagBookAcquisitionWork.Kind or RagBookPreparation.Kind ? state
            : _backgroundOperations?.FindOperation(RagBookPreparation.Kind) ?? _backgroundOperations?.FindOperation(RagBookAcquisitionWork.Kind);
        _applicationTray?.SetRagBooksStatus(books is not null ? L("RagBooks.State." + books.Phase) : null);
        NotifyRagBooksWaiting();
    }

    private async void ResumePausedOperation(string id)
    {
        try
        {
            _backgroundCountdown.Cancel();
            var job = _backgroundOperations?.PausedOperations.FirstOrDefault(state => state.Id == id);
            if (job is null) return;
            if (job.Kind is RagBookPreparation.Kind or RagBookAcquisitionWork.Kind) RevealRagBooksPage(job.Kind == RagBookAcquisitionWork.Kind);
            else RestoreFromTray();
            await _backgroundOperations!.ResumeAsync(id, _backgroundLifetime.Token);
        }
        catch (OperationCanceledException) when (_backgroundLifetime.IsCancellationRequested) { }
        catch (Exception error) { ReportBackgroundFailure(error); }
        finally { RefreshBackgroundTray(); }
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
            if (state?.Kind is RagBookAcquisitionWork.Kind or RagBookPreparation.Kind)
            {
                if (state.RequiresDecision) RevealRagBooksPage(state.Kind == RagBookAcquisitionWork.Kind);
                if (_backgroundOperations.HasLiveOperation(state.Id)) await _backgroundOperations.ResumeAsync(_backgroundLifetime.Token);
                else QueueRagBooksResume();
                return; // Observe restored work separately so the tray remains able to pause it.
            }
            if (state is { RequiresDecision: true, Kind: Services.Books.Smart.BookSmartWork.Kind })
            { RestoreFromTray(); OpenBookConversionUtility(); }
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
            if (notice.Kind == Services.Books.Smart.BookSmartWork.Kind)
            {
                var result = _backgroundOperations!.LoadResult(notice.Id) ?? _backgroundOperations.State!;
                RestoreFromTray(); OpenBookConversionUtility(); _bookConversionWindow!.ViewSmartResult(result);
                _backgroundOperations.Acknowledge(notice.Id); return;
            }
            if (notice.Kind == Services.LyricVideo.ClipWork.Kind && notice.Project is { } clipPath)
            {
                RestoreFromTray();
                _fileViewerService.Open(this, clipPath, _appSettings.FileViewer, _isDarkTheme, _localizationService);
                _backgroundOperations!.Acknowledge(notice.Id); return;
            }
            if (notice.Kind == Services.VideoEditor.VideoWork.Kind)
            {
                var state = _backgroundOperations!.LoadResult(notice.Id) ?? _backgroundOperations.State!;
                var request = System.Text.Json.JsonSerializer.Deserialize<Services.VideoEditor.VideoWorkRequest>(state.Input);
                if (request is null) return;
                RestoreFromTray();
                if (request.Action == "export" && request.Destination is {} output && System.IO.File.Exists(output))
                    _fileViewerService.Open(this, output, _appSettings.FileViewer, _isDarkTheme, _localizationService);
                else
                {
                    var project = Services.VideoEditor.VideoProjectStore.Load(request.ProjectPath);
                    var result = System.IO.Path.Combine(Services.VideoEditor.VideoProjectStore.Work(project.Id), "Operations", request.Id, "result.lopataVideo");
                    OpenVideoEditorUtility(System.IO.File.Exists(result) ? result : request.ProjectPath);
                }
                _backgroundOperations.Acknowledge(notice.Id); return;
            }
            if (notice.Kind == MusicRhymeWork.Kind)
            {
                RestoreFromTray(); OpenMusicScenario(false); MusicPage.ViewRhymeResult(notice.Project);
                _backgroundOperations!.Acknowledge(notice.Id); return;
            }
            if (notice.Kind == Services.MusicLibrary.LyricLibrarySearch.BackgroundKind)
            {
                ViewLyricLibrary(); _backgroundOperations!.Acknowledge(notice.Id); return;
            }
            if (notice.Kind == MusicCoverWork.Kind)
            {
                var result = _backgroundOperations!.LoadResult(notice.Id) ?? _backgroundOperations.State!;
                RestoreFromTray(); OpenMusicScenario(false); MusicPage.ViewCoverResult(result);
                _backgroundOperations.Acknowledge(notice.Id); return;
            }
            if (notice.Kind is RagBookAcquisitionWork.Kind or RagBookPreparation.Kind)
            {
                RevealRagBooksPage(notice.Kind == RagBookAcquisitionWork.Kind); _backgroundOperations!.Acknowledge(notice.Id); return;
            }
            if(notice.Kind==Services.MusicAceIdeas.MusicAceIdeaWork.Kind && notice.Project is not null) {
                RestoreFromTray();OpenMusicScenario(false);MusicPage.ViewAceIdeaResult(notice.Project);
                _backgroundOperations!.Acknowledge(notice.Id);return;
            }
            if(notice.Kind==Services.MusicScore.MusicScorePlanWork.Kind && notice.Project is not null) {
                RestoreFromTray();OpenMusicScenario(false);MusicPage.ViewScoreResult(notice.Project);
                _backgroundOperations!.Acknowledge(notice.Id);return;
            }
            if (notice.Kind is MusicGenerationRunner.BackgroundKind or MusicLyricsSynchronization.BackgroundKind && notice.Project is not null)
            {
                RestoreFromTray(); OpenMusicScenario(false); MusicPage.ViewGenerationResult(notice.Project);
                _backgroundOperations!.Acknowledge(notice.Id); return;
            }
            if (notice.Kind == FinancialAnalysisPlan.BackgroundKind)
            {
                if (ViewFinancialBackgroundResult(notice)) _backgroundOperations!.Acknowledge(notice.Id);
                return;
            }
            if (notice.Kind == Services.VectorEditor.VectorEditorGeneration.BackgroundKind && notice.Project is not null)
            {
                var job=Services.VectorGraphics.VectorStore.Load(notice.Project);
                if(job.Request.Editor is {} context){
                    var window=_vectorEditors.FirstOrDefault(w=>w.ProjectId==context.ProjectId)
                        ?? OpenVectorEditor(Services.VectorEditor.VectorProjectArchive.Load(Services.VectorEditor.VectorProjectRecovery.PathFor(context.ProjectId)));
                    window.Show();window.Activate();_backgroundOperations!.Acknowledge(notice.Id);
                }
                return;
            }
            if (notice.Kind == Services.VectorGraphics.VectorStore.BackgroundKind && notice.Project is not null)
            {
                RestoreFromTray(); ShowBackgroundScenarioPage(WorkStartPage); OpenVectorGenerationScenario();
                VectorPage.ViewResult(notice.Project); _backgroundOperations!.Acknowledge(notice.Id); return;
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
