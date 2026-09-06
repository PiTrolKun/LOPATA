using System.IO;
using System.Windows;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;
using WpfMessageBox = System.Windows.MessageBox;
using WpfOpenFileDialog = Microsoft.Win32.OpenFileDialog;
using WpfSaveFileDialog = Microsoft.Win32.SaveFileDialog;
using Media = System.Windows.Media;

namespace AIHub;

public partial class MainWindow
{
    private string _imageAnalysisMatrixRole = string.Empty;
    private bool _restartHeavyAnalysisAfterLanguageChange;
    private DispatcherTimer? _heavyResourceMonitorTimer;
    private CancellationTokenSource? _heavyResourceMonitorCts;
    private bool _heavyResourceMonitorBusy;
    private bool _heavyResourceWarningShown;

    private void ShowImageAnalysisSubscenarioSelection()
    {
        _sessionAudioPlayer?.Clear();
        CancelImageAnalysisSpeech();
        SaveCurrentImageAnalysisSession();
        _imageAnalysisLiterarySession = null;
        var bundleId = _selectedImageAnalysisBundle?.Id ?? ImageAnalysisBundleCatalog.MediumId;
        ImageAnalysisWorkspacePage.ShowSubscenarioSelection(
            _imageAnalysisSessionStore.LoadAll(_storageSettings)
                .Where(session => string.Equals(session.BundleId, bundleId, StringComparison.Ordinal))
                .ToList());
    }

    private void ImageAnalysisWorkspacePage_SingleSubscenarioRequested(object? sender, EventArgs e)
    {
        var bundleId = _selectedImageAnalysisBundle?.Id ?? ImageAnalysisBundleCatalog.MediumId;
        var isHeavy = ImageAnalysisModeCapabilities.UsesOmniConversation(bundleId);
        _imageAnalysisLiterarySession = new ImageAnalysisLiterarySession
        {
            BundleId = bundleId,
            PipelineId = ImageAnalysisModeCapabilities.Pipeline(bundleId),
            PipelineVersion = isHeavy
                ? ImageAnalysisPipelineIds.OmniHeavyVersion
                : ImageAnalysisPipelineIds.LegacyVersion,
            ContractVersion = ImageAnalysisPipelineIds.ContractVersion,
            ModelId = isHeavy ? ManagedModelCatalog.Qwen25OmniRepository : string.Empty,
            ModelRevision = isHeavy ? ManagedModelCatalog.Qwen25OmniRevision : string.Empty,
            RuntimeId = isHeavy
                ? ImageAnalysisRuntimeIds.Qwen25OmniTransformers
                : ImageAnalysisRuntimeIds.Legacy,
            CurrentStep = ImageAnalysisLiterarySteps.Image,
            Status = ImageAnalysisLiteraryStatuses.Draft,
            Settings = new ImageAnalysisLiterarySettings
            {
                LanguageCode = _appSettings.LanguageCode
            }
        };
        OmniLlamaProfile.ForBundle(bundleId)?.ApplyToNewSession(_imageAnalysisLiterarySession);
        _imageAnalysisSessionStore.Save(_imageAnalysisLiterarySession, _storageSettings);
        ImageAnalysisWorkspacePage.ShowImageStep(_imageAnalysisLiterarySession);
        StatusText.Text = L("Status.ImageAnalysisChooseFile");
    }

    private async void ImageAnalysisWorkspacePage_SelectImageRequested(object? sender, EventArgs e)
    {
        if (_imageAnalysisLiterarySession is null || _imageAnalysisLiterarySession.ContextBlocked
            || OmniSessionCompatibility.IsLegacyBeta(_imageAnalysisLiterarySession))
        {
            return;
        }
        var dialog = new WpfOpenFileDialog
        {
            Title = L("ImageAnalysis.Workspace.DialogTitle"),
            Filter = L("ImageAnalysis.Workspace.DialogFilter"),
            Multiselect = false,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true || !File.Exists(dialog.FileName))
        {
            return;
        }

        CancelImageAnalysisLiteraryOperation();
        _imageAnalysisLiteraryCts = new CancellationTokenSource();
        AddImageAnalysisEvent(
            _imageAnalysisLiterarySession,
            ImageAnalysisEventCodes.FileCheckStarted,
            string.Empty,
            ImageAnalysisEventStatuses.Active,
            Path.GetFileName(dialog.FileName));
        ImageAnalysisWorkspacePage.ShowFileChecking(dialog.FileName);
        ImageAnalysisWorkspacePage.RefreshActivity(_imageAnalysisLiterarySession);
        try
        {
            var passport = await _imageAnalysisFileValidationService.ValidateAsync(
                dialog.FileName,
                _imageAnalysisLiteraryCts.Token);
            _imageAnalysisLiterarySession.File = passport;
            _imageAnalysisLiterarySession.VisualReport = string.Empty;
            _imageAnalysisLiterarySession.HiddenConversation.Clear();
            _imageAnalysisLiterarySession.AnalysisLanguageCode = string.Empty;
            _imageAnalysisLiterarySession.Observations.Clear();
            _imageAnalysisLiterarySession.ReviewSummary = new ImageAnalysisReviewSummary();
            _imageAnalysisLiterarySession.Events.Clear();
            _imageAnalysisLiterarySession.Versions.Clear();
            _imageAnalysisLiterarySession.SelectedVersionId = string.Empty;
            _imageAnalysisLiterarySession.CompletedAt = null;
            _imageAnalysisLiterarySession.InternalImageCopyPath = string.Empty;
            _imageAnalysisLiterarySession.InternalDescriptionCopyPath = string.Empty;
            _imageAnalysisLiterarySession.Status = ImageAnalysisLiteraryStatuses.FileReady;
            _imageAnalysisLiterarySession.CurrentStep = ImageAnalysisLiterarySteps.Image;
            _imageAnalysisLiterarySession.LastError = string.Empty;
            AddImageAnalysisEvent(
                _imageAnalysisLiterarySession,
                ImageAnalysisEventCodes.FileCheckStarted,
                string.Empty,
                ImageAnalysisEventStatuses.Completed,
                passport.DisplayName);
            AddImageAnalysisEvent(
                _imageAnalysisLiterarySession,
                ImageAnalysisEventCodes.FileReady,
                string.Empty,
                ImageAnalysisEventStatuses.Completed,
                passport.DisplayName);
            _imageAnalysisSessionStore.Save(_imageAnalysisLiterarySession, _storageSettings);
            ImageAnalysisWorkspacePage.SetValidatedFile(_imageAnalysisLiterarySession);
            StatusText.Text = LF("Status.ImageAnalysisFileSelected", passport.DisplayName);
        }
        catch (OperationCanceledException)
        {
            AddImageAnalysisEvent(
                _imageAnalysisLiterarySession,
                ImageAnalysisEventCodes.FileRejected,
                string.Empty,
                ImageAnalysisEventStatuses.Failed,
                L("ImageAnalysis.Workspace.FileCancelled"));
            _imageAnalysisSessionStore.Save(_imageAnalysisLiterarySession, _storageSettings);
            if (_imageAnalysisLiterarySession.File is null)
            {
                ImageAnalysisWorkspacePage.SetFileError(L("ImageAnalysis.Workspace.FileCancelled"));
                ImageAnalysisWorkspacePage.RefreshActivity(_imageAnalysisLiterarySession);
            }
            else
            {
                ImageAnalysisWorkspacePage.ShowSession(_imageAnalysisLiterarySession);
                ImageAnalysisWorkspacePage.SetOperationError(L("ImageAnalysis.Workspace.FileCancelled"));
            }
        }
        catch (Exception ex)
        {
            _imageAnalysisLiterarySession.LastError = ex.Message;
            AddImageAnalysisEvent(
                _imageAnalysisLiterarySession,
                ImageAnalysisEventCodes.FileRejected,
                string.Empty,
                ImageAnalysisEventStatuses.Failed,
                ex.Message);
            _imageAnalysisSessionStore.Save(_imageAnalysisLiterarySession, _storageSettings);
            if (_imageAnalysisLiterarySession.File is null)
            {
                ImageAnalysisWorkspacePage.SetFileError(ex.Message);
            }
            else
            {
                ImageAnalysisWorkspacePage.ShowSession(_imageAnalysisLiterarySession);
                ImageAnalysisWorkspacePage.SetOperationError(ex.Message);
            }
            StatusText.Text = L("Status.ImageAnalysisFileRejected");
        }
        finally
        {
            _imageAnalysisLiteraryCts?.Dispose();
            _imageAnalysisLiteraryCts = null;
        }
    }

    private void ImageAnalysisWorkspacePage_DraftSettingsChanged(object? sender, EventArgs e)
    {
        if (_imageAnalysisLiterarySession is null) return;
        try { _imageAnalysisSessionStore.Save(_imageAnalysisLiterarySession, _storageSettings); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        { StatusText.Text = L("PromptPairs.StorageError"); }
    }

    private async void ImageAnalysisWorkspacePage_GenerateRequested(
        object? sender,
        ImageAnalysisSettingsRequestedEventArgs e)
    {
        if (_imageAnalysisLiterarySession?.File is null || _imageAnalysisLiterarySession.ContextBlocked
            || OmniSessionCompatibility.IsLegacyBeta(_imageAnalysisLiterarySession))
        {
            return;
        }

        try
        {
            if (e.Settings.PromptMode != PromptModes.Standard
                && !ImageAnalysisModeCapabilities.UsesOmniConversation(_imageAnalysisLiterarySession.BundleId))
                throw new System.IO.InvalidDataException("PromptPairs.Incompatible");
            OmniPromptPairAdapter.Validate(e.Settings);
        }
        catch (System.IO.InvalidDataException ex)
        {
            StatusText.Text = L(ex.Message);
            return;
        }

        CancelImageAnalysisLiteraryOperation();
        var owner = new CancellationTokenSource();
        _imageAnalysisLiteraryCts = owner;
        var acceptProgress = true;
        var session = _imageAnalysisLiterarySession;
        session.Settings = e.Settings;
        session.Settings.LanguageCode = _appSettings.LanguageCode;
        session.CurrentStep = ImageAnalysisLiterarySteps.Result;
        session.Status = ImageAnalysisLiteraryStatuses.AnalysingVision;
        session.LastError = string.Empty;
        AddImageAnalysisEvent(
            session,
            ImageAnalysisEventCodes.VisionStarted,
            ManagedModelRoles.Vision,
            ImageAnalysisEventStatuses.Active,
            session.File.DisplayName);
        _imageAnalysisSessionStore.Save(session, _storageSettings);
        ImageAnalysisWorkspacePage.ShowSession(session);
        ImageAnalysisWorkspacePage.SetBusy(
            ManagedModelRoles.Vision,
            L("ImageAnalysis.Workspace.Activity.VisionActive"));
        StartImageAnalysisMatrix(ManagedModelRoles.Vision);
        StatusText.Text = L("Status.ImageAnalysisVisionRunning");

        var progress = new Progress<ImageAnalysisLiteraryProgress>(value =>
        {
            if (!acceptProgress
                || owner.IsCancellationRequested
                || !ReferenceEquals(_imageAnalysisLiteraryCts, owner))
            {
                return;
            }
            if (value.Stage == OmniResponseRecovery.WaitingStage)
            {
                var waiting = L("ImageAnalysis.Context.RetryWaiting");
                ImageAnalysisWorkspacePage.SetBusy(ManagedModelRoles.Core, waiting);
                StatusText.Text = waiting;
                return;
            }
            session.Status = value.Role == ManagedModelRoles.Vision
                ? ImageAnalysisLiteraryStatuses.AnalysingVision
                : ImageAnalysisLiteraryStatuses.Writing;
            var message = value.Role == ManagedModelRoles.Vision
                ? L("ImageAnalysis.Workspace.Activity.VisionActive")
                : L("ImageAnalysis.Workspace.Activity.CoreActive");
            ImageAnalysisWorkspacePage.SetBusy(value.Role, message);
            if (value.Role == ManagedModelRoles.Core)
            {
                AddImageAnalysisEvent(
                    session,
                    ImageAnalysisEventCodes.CoreStarted,
                    ManagedModelRoles.Core,
                    ImageAnalysisEventStatuses.Active,
                    L("ImageAnalysis.Workspace.Activity.CoreActive"));
            }
            StartImageAnalysisMatrix(value.Role);
            ImageAnalysisWorkspacePage.RefreshActivity(session);
            StatusText.Text = value.Role == ManagedModelRoles.Vision
                ? L("Status.ImageAnalysisVisionRunning")
                : L("Status.ImageAnalysisCoreRunning");
        });
        var stream = new Progress<ModelStreamChunk>(chunk =>
        {
            if (!acceptProgress
                || owner.IsCancellationRequested
                || !ReferenceEquals(_imageAnalysisLiteraryCts, owner))
            {
                return;
            }
            if (!string.IsNullOrWhiteSpace(chunk.Text))
            {
                ChoiceMatrixRain.Feed(chunk.Text);
            }
        });

        try
        {
            var result = await GetImageAnalysisLiteraryPipeline(session).CreateAsync(
                session.File,
                session.Settings,
                _storageSettings,
                session,
                LogImageAnalysisRuntime,
                progress,
                stream,
                checkpoint =>
                {
                    void ApplyCheckpoint()
                    {
                        session.VisualReport = checkpoint.VisualReport;
                        session.HiddenConversation = checkpoint.HiddenConversation.ToList();
                        session.Observations.Clear();
                        session.Status = ImageAnalysisLiteraryStatuses.Writing;
                        AddImageAnalysisEvent(
                            session,
                            ImageAnalysisEventCodes.VisionCompleted,
                            ManagedModelRoles.Vision,
                            ImageAnalysisEventStatuses.Completed,
                            L("ImageAnalysis.Workspace.Activity.VisionReportReady"));
                        _imageAnalysisSessionStore.Save(session, _storageSettings);
                        ImageAnalysisWorkspacePage.RefreshActivity(session);
                    }

                    if (Dispatcher.CheckAccess())
                    {
                        ApplyCheckpoint();
                    }
                    else
                    {
                        Dispatcher.Invoke(ApplyCheckpoint);
                    }
                },
                owner.Token);
            session.VisualReport = result.VisualReport;
            if (result.HiddenConversation is not null)
            {
                session.HiddenConversation = result.HiddenConversation.ToList();
            }
            session.RuntimeMetrics.VisualPassMilliseconds = result.VisualPassMilliseconds;
            session.RuntimeMetrics.ComposePassMilliseconds = result.ComposePassMilliseconds;
            session.ReviewSummary = result.ReviewSummary;
            AddImageAnalysisVersion(session, result.Description, string.Empty, "initial");
            session.Status = ImageAnalysisLiteraryStatuses.ResultReady;
            session.CurrentStep = ImageAnalysisLiterarySteps.Result;
            session.LastError = string.Empty;
            AddImageAnalysisEvent(
                session,
                ImageAnalysisEventCodes.DescriptionReady,
                ManagedModelRoles.Core,
                ImageAnalysisEventStatuses.Completed,
                LF("ImageAnalysis.Workspace.Result.VersionName", session.Versions.Count, DateTime.Now.ToString("g")));
            _imageAnalysisSessionStore.Save(session, _storageSettings);
            var delaySummaryReveal = !ImageAnalysisModeCapabilities.UsesOmniConversation(session.BundleId)
                && ImageAnalysisSpeechTextService.ShouldDelaySummaryReveal(
                ImageAnalysisModeCapabilities.UsesOmniConversation(session.BundleId)
                    ? GetHeavyImageAnalysisSpeechSettings().Mode
                    : _appSettings.ImageAnalysisSpeech?.Mode,
                session.ReviewSummary);
            ImageAnalysisWorkspacePage.ShowSession(
                session,
                showReviewSummary: !delaySummaryReveal);
            StatusText.Text = session.ContextBlocked ? L("ImageAnalysis.Context.RestartSession") : L("Status.ImageAnalysisResultReady");
            _ = SpeakCurrentImageAnalysisSummaryAsync(
                automatic: true,
                playbackStarted: delaySummaryReveal
                    ? () => ImageAnalysisWorkspacePage.RevealReviewSummary(session)
                    : null);
        }
        catch (OperationCanceledException)
        {
            CompleteActiveImageAnalysisEvents(session);
            session.Status = session.Versions.Count > 0
                ? ImageAnalysisLiteraryStatuses.ResultReady
                : ImageAnalysisLiteraryStatuses.FileReady;
            session.LastError = string.Empty;
            _imageAnalysisSessionStore.Save(session, _storageSettings);
            ImageAnalysisWorkspacePage.ShowSession(session);
            StatusText.Text = L("Status.ImageAnalysisCancelled");
        }
        catch (Exception ex)
        {
            session.Status = ImageAnalysisLiteraryStatuses.Failed;
            var errorMessage = ex switch
            {
                ImageAnalysisContextExhaustedException => L("ImageAnalysis.Context.RestartSession"),
                ImageAnalysisOmniFormatException => L("ImageAnalysis.Context.RetryFailed"),
                ImageAnalysisModelChangedException => L("ImageAnalysis.ModelChanged"),
                _ => ex.Message
            };
            session.CurrentStep = session.Versions.Count > 0
                ? ImageAnalysisLiterarySteps.Result
                : ImageAnalysisLiterarySteps.Settings;
            session.LastError = errorMessage;
            AddImageAnalysisEvent(
                session,
                ImageAnalysisEventCodes.OperationFailed,
                string.Empty,
                ImageAnalysisEventStatuses.Failed,
                errorMessage);
            _imageAnalysisSessionStore.Save(session, _storageSettings);
            if (session.Versions.Count == 0)
            {
                ImageAnalysisWorkspacePage.ShowSettings(session);
            }
            ImageAnalysisWorkspacePage.SetOperationError(errorMessage);
            StatusText.Text = LF("Status.ImageAnalysisFailed", errorMessage);
        }
        finally
        {
            acceptProgress = false;
            ImageAnalysisWorkspacePage.StopActivity();
            StopImageAnalysisMatrix();
            if (ReferenceEquals(_imageAnalysisLiteraryCts, owner))
            {
                _imageAnalysisLiteraryCts = null;
            }
            owner.Dispose();
            RestartHeavyAnalysisAfterLanguageChangeIfNeeded(session);
        }
    }

    private async void ImageAnalysisWorkspacePage_ReviseRequested(
        object? sender,
        ImageAnalysisRevisionRequestedEventArgs e)
    {
        if (_imageAnalysisLiterarySession?.GetSelectedVersion() is null || _imageAnalysisLiterarySession.ContextBlocked
            || OmniSessionCompatibility.IsLegacyBeta(_imageAnalysisLiterarySession))
        {
            return;
        }
        CancelImageAnalysisLiteraryOperation();
        var owner = new CancellationTokenSource();
        _imageAnalysisLiteraryCts = owner;
        var acceptProgress = true;
        var session = _imageAnalysisLiterarySession;
        session.Status = ImageAnalysisLiteraryStatuses.Revising;
        AddImageAnalysisEvent(
            session,
            ImageAnalysisEventCodes.RevisionStarted,
            ManagedModelRoles.Core,
            ImageAnalysisEventStatuses.Active,
            e.Request);
        _imageAnalysisSessionStore.Save(session, _storageSettings);
        ImageAnalysisWorkspacePage.SetBusy(
            ManagedModelRoles.Core,
            L("ImageAnalysis.Workspace.Activity.RevisionActive"));
        StartImageAnalysisMatrix(ManagedModelRoles.Core);
        StatusText.Text = L("Status.ImageAnalysisRevisionRunning");
        var progress = new Progress<ImageAnalysisLiteraryProgress>(value =>
        {
            if (!acceptProgress
                || owner.IsCancellationRequested
                || !ReferenceEquals(_imageAnalysisLiteraryCts, owner))
            {
                return;
            }
            if (value.Stage == OmniResponseRecovery.WaitingStage)
            {
                var waiting = L("ImageAnalysis.Context.RetryWaiting");
                ImageAnalysisWorkspacePage.SetBusy(ManagedModelRoles.Core, waiting);
                StatusText.Text = waiting;
                return;
            }
            ImageAnalysisWorkspacePage.SetBusy(
                ManagedModelRoles.Core,
                L("ImageAnalysis.Workspace.Activity.RevisionActive"));
            StartImageAnalysisMatrix(ManagedModelRoles.Core);
        });
        var stream = new Progress<ModelStreamChunk>(chunk =>
        {
            if (!acceptProgress
                || owner.IsCancellationRequested
                || !ReferenceEquals(_imageAnalysisLiteraryCts, owner))
            {
                return;
            }
            if (!string.IsNullOrWhiteSpace(chunk.Text))
            {
                ChoiceMatrixRain.Feed(chunk.Text);
            }
        });
        try
        {
            var revised = await GetImageAnalysisLiteraryPipeline(session).ReviseAsync(
                session,
                e.Request,
                _storageSettings,
                LogImageAnalysisRuntime,
                progress,
                stream,
                owner.Token);
            AddImageAnalysisVersion(session, revised, e.Request, "revision");
            session.Status = ImageAnalysisLiteraryStatuses.ResultReady;
            session.LastError = string.Empty;
            AddImageAnalysisEvent(
                session,
                ImageAnalysisEventCodes.RevisionReady,
                ManagedModelRoles.Core,
                ImageAnalysisEventStatuses.Completed,
                LF("ImageAnalysis.Workspace.Result.VersionName", session.Versions.Count, DateTime.Now.ToString("g")));
            _imageAnalysisSessionStore.Save(session, _storageSettings);
            ImageAnalysisWorkspacePage.ShowSession(session);
            StatusText.Text = session.ContextBlocked ? L("ImageAnalysis.Context.RestartSession") : L("Status.ImageAnalysisRevisionReady");
        }
        catch (OperationCanceledException)
        {
            CompleteActiveImageAnalysisEvents(session);
            session.Status = ImageAnalysisLiteraryStatuses.ResultReady;
            _imageAnalysisSessionStore.Save(session, _storageSettings);
            ImageAnalysisWorkspacePage.ShowSession(session);
            StatusText.Text = L("Status.ImageAnalysisCancelled");
        }
        catch (Exception ex)
        {
            session.Status = ImageAnalysisLiteraryStatuses.ResultReady;
            var errorMessage = ex switch
            {
                ImageAnalysisContextExhaustedException => L("ImageAnalysis.Context.RestartSession"),
                ImageAnalysisOmniFormatException => L("ImageAnalysis.Context.RetryFailed"),
                ImageAnalysisModelChangedException => L("ImageAnalysis.ModelChanged"),
                _ => ex.Message
            };
            session.LastError = errorMessage;
            AddImageAnalysisEvent(
                session,
                ImageAnalysisEventCodes.OperationFailed,
                string.Empty,
                ImageAnalysisEventStatuses.Failed,
                errorMessage);
            _imageAnalysisSessionStore.Save(session, _storageSettings);
            ImageAnalysisWorkspacePage.SetOperationError(errorMessage);
            StatusText.Text = LF("Status.ImageAnalysisFailed", errorMessage);
        }
        finally
        {
            acceptProgress = false;
            ImageAnalysisWorkspacePage.StopActivity();
            StopImageAnalysisMatrix();
            if (ReferenceEquals(_imageAnalysisLiteraryCts, owner))
            {
                _imageAnalysisLiteraryCts = null;
            }
            owner.Dispose();
            RestartHeavyAnalysisAfterLanguageChangeIfNeeded(session);
        }
    }

    private void RestartHeavyAnalysisAfterLanguageChangeIfNeeded(
        ImageAnalysisLiterarySession session)
    {
        if (!_restartHeavyAnalysisAfterLanguageChange
            || !ImageAnalysisModeCapabilities.UsesOmniConversation(session.BundleId)
            || session.File is null)
        {
            return;
        }

        _restartHeavyAnalysisAfterLanguageChange = false;
        session.Settings.LanguageCode = _appSettings.LanguageCode;
        session.VisualReport = string.Empty;
        session.HiddenConversation.Clear();
        session.AnalysisLanguageCode = string.Empty;
        _imageAnalysisSessionStore.Save(session, _storageSettings);
        _ = Dispatcher.BeginInvoke(() => ImageAnalysisWorkspacePage_GenerateRequested(
            ImageAnalysisWorkspacePage,
            new ImageAnalysisSettingsRequestedEventArgs(session.Settings)));
    }

    private void ImageAnalysisWorkspacePage_PreviewRequested(object? sender, EventArgs e)
    {
        var version = _imageAnalysisLiterarySession?.GetSelectedVersion();
        if (version is null)
        {
            return;
        }
        var window = new ImageAnalysisPreviewWindow(
            L("ImageAnalysis.Preview.Title"),
            L("Common.Close"),
            BuildImageAnalysisMarkdown(version));
        window.Owner = this;
        window.ShowDialog();
    }

    private void ImageAnalysisWorkspacePage_ExportRequested(object? sender, EventArgs e)
    {
        var session = _imageAnalysisLiterarySession;
        var version = session?.GetSelectedVersion();
        if (session is null || version is null)
        {
            return;
        }
        var dialog = new WpfSaveFileDialog
        {
            Title = L("ImageAnalysis.Export.Title"),
            Filter = L("ImageAnalysis.Export.Filter"),
            DefaultExt = ".docx",
            AddExtension = true,
            FileName = $"{L("App.ProductName")}_Описание_{DateTime.Now:yyyyMMdd_HHmm}.docx",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        try
        {
            ExecutorDocxExporter.Export(new ExecutorResultSnapshot
            {
                Id = version.VersionId,
                Version = version.Number,
                CreatedAt = version.CreatedAt,
                Title = L("ImageAnalysis.Preview.Title"),
                Markdown = BuildImageAnalysisMarkdown(version),
                IsFinal = session.Status == ImageAnalysisLiteraryStatuses.Completed
            }, dialog.FileName);
            if (!session.ExportedFiles.Contains(dialog.FileName, StringComparer.OrdinalIgnoreCase))
            {
                session.ExportedFiles.Add(dialog.FileName);
            }
            AddImageAnalysisEvent(
                session,
                ImageAnalysisEventCodes.ExportCompleted,
                string.Empty,
                ImageAnalysisEventStatuses.Completed,
                Path.GetFileName(dialog.FileName));
            _imageAnalysisSessionStore.Save(session, _storageSettings);
            ImageAnalysisWorkspacePage.RefreshActivity(session);
            StatusText.Text = LF("Status.ImageAnalysisExported", dialog.FileName);
        }
        catch (Exception ex)
        {
            StatusText.Text = LF("Status.ImageAnalysisExportFailed", ex.Message);
        }
    }

    private async void ImageAnalysisWorkspacePage_CompleteRequested(object? sender, EventArgs e)
    {
        var session = _imageAnalysisLiterarySession;
        if (session?.GetSelectedVersion() is null)
        {
            return;
        }
        var choice = WpfMessageBox.Show(
            this,
            L("ImageAnalysis.Complete.BackupQuestion"),
            L("ImageAnalysis.Complete.Title"),
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);
        if (choice == MessageBoxResult.Cancel)
        {
            return;
        }
        try
        {
            if (choice == MessageBoxResult.Yes)
            {
                await _imageAnalysisSessionStore.CreateInternalBackupAsync(
                    session,
                    _storageSettings,
                    CancellationToken.None);
            }
            CancelImageAnalysisSpeech();
            _sessionAudioPlayer?.Clear();
            session.Status = ImageAnalysisLiteraryStatuses.Completed;
            session.CompletedAt = DateTimeOffset.Now;
            session.CurrentStep = ImageAnalysisLiterarySteps.Result;
            AddImageAnalysisEvent(
                session,
                ImageAnalysisEventCodes.SessionCompleted,
                string.Empty,
                ImageAnalysisEventStatuses.Completed,
                choice == MessageBoxResult.Yes
                    ? L("Status.ImageAnalysisCompletedWithBackup")
                    : L("Status.ImageAnalysisCompleted"));
            _imageAnalysisSessionStore.Save(session, _storageSettings);
            ImageAnalysisWorkspacePage.ShowSession(session);
            CancelImageAnalysisRuntimePreparation(stopModels: true);
            StatusText.Text = choice == MessageBoxResult.Yes
                ? L("Status.ImageAnalysisCompletedWithBackup")
                : L("Status.ImageAnalysisCompleted");
        }
        catch (Exception ex)
        {
            StatusText.Text = LF("Status.ImageAnalysisBackupFailed", ex.Message);
        }
    }

    private void ImageAnalysisWorkspacePage_CancelRequested(object? sender, EventArgs e) =>
        _imageAnalysisLiteraryCts?.Cancel();

    private void ImageAnalysisWorkspacePage_NewAnalysisRequested(object? sender, EventArgs e)
    {
        CancelImageAnalysisLiteraryOperation();
        StopImageAnalysisMatrix();
        CancelImageAnalysisSpeech();
        ShowImageAnalysisSubscenarioSelection();
        BeginImageAnalysisRuntimePreparation();
    }

    private void ImageAnalysisWorkspacePage_HomeRequested(object? sender, EventArgs e)
    {
        CancelImageAnalysisLiteraryOperation();
        StopImageAnalysisMatrix();
        CancelImageAnalysisRuntimePreparation(stopModels: true);
        StopImageAnalysisSpeechSession();
        SaveCurrentImageAnalysisSession();
        ShowWorkStartPage();
        StatusText.Text = L("Status.WorkStartOpened");
    }

    private void ImageAnalysisWorkspacePage_ResumeRequested(
        object? sender,
        ImageAnalysisSessionRequestedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.SessionId))
        {
            ShowImageAnalysisSubscenarioSelection();
            return;
        }
        var session = _imageAnalysisSessionStore.Load(e.SessionId, _storageSettings);
        if (session is null)
        {
            StatusText.Text = L("Status.ImageAnalysisSessionMissing");
            ShowImageAnalysisSubscenarioSelection();
            return;
        }
        CancelImageAnalysisSpeech();
        _sessionAudioPlayer?.Clear();
        _imageAnalysisLiterarySession = session;
        if (session.CurrentStep == ImageAnalysisLiterarySteps.Subscenario)
        {
            session.CurrentStep = ImageAnalysisLiterarySteps.Image;
        }
        if (OmniSessionCompatibility.IsLegacyBeta(session))
            CancelImageAnalysisRuntimePreparation(stopModels: true);
        ImageAnalysisWorkspacePage.ShowSession(session);
        RefreshImageAnalysisSpeechUi();
        ImageAnalysisWorkspacePage.SetReadOnlyMode(_imageAnalysisWorkspaceReadOnly);
        StatusText.Text = L(OmniSessionCompatibility.IsLegacyBeta(session) ? "ImageAnalysis.ModelChanged" : _imageAnalysisWorkspaceReadOnly
            ? "Status.ImageAnalysisHistoryReadOnly"
            : "Status.ImageAnalysisSessionResumed");
    }

    private void ImageAnalysisWorkspacePage_VersionRequested(
        object? sender,
        ImageAnalysisVersionRequestedEventArgs e)
    {
        var session = _imageAnalysisLiterarySession;
        if (session is null || session.Versions.All(version => version.VersionId != e.VersionId))
        {
            return;
        }
        session.SelectedVersionId = e.VersionId;
        _imageAnalysisSessionStore.Save(session, _storageSettings);
        ImageAnalysisWorkspacePage.ShowSession(session);
    }

    private ISingleImageLiteraryPipeline GetImageAnalysisLiteraryPipeline(
        ImageAnalysisLiterarySession? session = null)
    {
        if (OmniSessionCompatibility.IsLegacyBeta(session)) throw new ImageAnalysisModelChangedException();
        var pipelineId = session?.PipelineId;
        if (string.IsNullOrWhiteSpace(pipelineId))
        {
            pipelineId = ImageAnalysisModeCapabilities.Pipeline(_selectedImageAnalysisBundle?.Id);
        }
        if (_imageAnalysisLiteraryPipeline is not null
            && string.Equals(_imageAnalysisLiteraryPipeline.PipelineId, pipelineId, StringComparison.Ordinal))
        {
            return _imageAnalysisLiteraryPipeline;
        }
        _imageAnalysisLiteraryPipeline?.Dispose();
        _imageAnalysisLiteraryPipeline = pipelineId switch
        {
            ImageAnalysisPipelineIds.OmniAlpha => new OmniHeavySingleImageLiteraryPipeline(
                new OmniLlamaRuntimeService(_imageAnalysisBundleInstallationService.LibraryStore)),
            ImageAnalysisPipelineIds.OmniBeta => new OmniHeavySingleImageLiteraryPipeline(
                new OmniLlamaRuntimeService(_imageAnalysisBundleInstallationService.LibraryStore, OmniLlamaProfile.Beta)),
            ImageAnalysisPipelineIds.OmniHeavy => new OmniHeavySingleImageLiteraryPipeline(
                new Qwen25OmniRuntimeService(_imageAnalysisBundleInstallationService.LibraryStore)),
            ImageAnalysisPipelineIds.Legacy => new LegacySingleImageLiteraryPipeline(
                new ImageAnalysisLiteraryService(
                    new ImageAnalysisKimiRuntimeService(_imageAnalysisBundleInstallationService.LibraryStore),
                    new LlamaServerRuntimeService(_userContextService))),
            _ => throw new InvalidDataException("Unknown image-analysis pipeline; saved work was not changed.")
        };
        return _imageAnalysisLiteraryPipeline;
    }

    private void AddImageAnalysisVersion(
        ImageAnalysisLiterarySession session,
        string text,
        string changeRequest,
        string source)
    {
        var version = new ImageAnalysisLiteraryVersion
        {
            Number = session.Versions.Count + 1,
            Text = text.Trim(),
            ChangeRequest = changeRequest,
            Source = source
        };
        session.Versions.Add(version);
        session.SelectedVersionId = version.VersionId;
    }

    private void AddImageAnalysisEvent(
        ImageAnalysisLiterarySession session,
        string code,
        string role,
        string status,
        string detail)
    {
        var last = session.Events.LastOrDefault();
        if (last is not null
            && last.Status == ImageAnalysisEventStatuses.Active
            && status == ImageAnalysisEventStatuses.Active
            && string.Equals(last.Code, code, StringComparison.Ordinal))
        {
            last.Detail = detail;
            return;
        }

        CompleteActiveImageAnalysisEvents(session);
        session.Events.Add(new ImageAnalysisEventEntry
        {
            Code = code,
            Role = role,
            Status = status,
            Detail = detail
        });
        if (session.Events.Count > 160)
        {
            session.Events.RemoveRange(0, session.Events.Count - 160);
        }
    }

    private static void CompleteActiveImageAnalysisEvents(ImageAnalysisLiterarySession session)
    {
        foreach (var item in session.Events.Where(item => item.Status == ImageAnalysisEventStatuses.Active))
        {
            item.Status = ImageAnalysisEventStatuses.Completed;
        }
    }

    private void StartImageAnalysisMatrix(string role)
    {
        if (string.Equals(_imageAnalysisMatrixRole, role, StringComparison.Ordinal)
            && ChoiceAiActivityPanel.Visibility == Visibility.Visible)
        {
            return;
        }
        _imageAnalysisMatrixRole = role;
        var isHeavy = ImageAnalysisModeCapabilities.UsesOmniConversation(_imageAnalysisLiterarySession?.BundleId)
            || ImageAnalysisModeCapabilities.UsesOmniConversation(_selectedImageAnalysisBundle?.Id);
        var color = isHeavy
            ? Media.Color.FromRgb(42, 210, 108)
            : role switch
            {
                ManagedModelRoles.Vision => Media.Color.FromRgb(59, 130, 246),
                ManagedModelRoles.Localizer => Media.Color.FromRgb(239, 68, 68),
                _ => Media.Color.FromRgb(42, 210, 108)
            };
        StartAiActivityOverlay(color);
    }

    private void StopImageAnalysisMatrix()
    {
        _imageAnalysisMatrixRole = string.Empty;
        StopAiActivityOverlay();
    }

    private void BeginImageAnalysisRuntimePreparation()
    {
        CancelImageAnalysisRuntimePreparation(stopModels: false);
        if (OmniSessionCompatibility.IsLegacyBeta(_imageAnalysisLiterarySession)) return;
        // Shared CPU speech warmup precedes model memory measurements.
        var speechWarmupTask = BeginImageAnalysisSpeechWarmup();
        var owner = new CancellationTokenSource();
        _imageAnalysisRuntimePreparationCts = owner;
        _imageAnalysisRuntimePreparationTask = PrepareImageAnalysisRuntimeAsync(owner, speechWarmupTask);
    }

    private async Task PrepareImageAnalysisRuntimeAsync(
        CancellationTokenSource owner,
        Task speechWarmupTask)
    {
        var cancellationToken = owner.Token;
        var prepareCoreConcurrently = ImageAnalysisRuntimePreparationPolicy
            .ShouldPrepareCoreConcurrently(_lastPassport);
        var isHeavy = ImageAnalysisModeCapabilities.UsesOmniConversation(_selectedImageAnalysisBundle?.Id)
            || ImageAnalysisModeCapabilities.UsesOmniConversation(_imageAnalysisLiterarySession?.BundleId);
        try
        {
            if (isHeavy)
            {
                LogImageAnalysisRuntime("Waiting for the existing Kokoro CPU warmup before measuring Heavy memory.");
                await speechWarmupTask;
                cancellationToken.ThrowIfCancellationRequested();
                LogImageAnalysisRuntime(
                    "Starting the Heavy smart resource measurement and fixed-placement Omni warmup.");
            }
            else
            {
                LogImageAnalysisRuntime(
                    "Waiting for the Kokoro warmup stage before preparing image-analysis runtimes.");
                await speechWarmupTask;
                cancellationToken.ThrowIfCancellationRequested();
                LogImageAnalysisRuntime(
                    $"Kokoro warmup stage finished; delaying image-analysis runtimes by " +
                    $"{ImageAnalysisRuntimePreparationPolicy.ModelStartDelay.TotalMilliseconds:F0} ms.");
                await Task.Delay(ImageAnalysisRuntimePreparationPolicy.ModelStartDelay, cancellationToken);
                LogImageAnalysisRuntime(prepareCoreConcurrently
                    ? "Preparing Kimi and core concurrently."
                    : "Preparing Kimi first; the core remains on-demand for the safe-memory profile.");
            }
            LogImageAnalysisRuntime(RuntimeResourceDiagnostics.DescribeSystemMemory(
                "before_image_analysis_runtime_preparation"));
            StatusText.Text = L("Status.ImageAnalysisModelsPreparing");
            await GetImageAnalysisLiteraryPipeline(_imageAnalysisLiterarySession).PrepareAsync(
                _storageSettings,
                _imageAnalysisLiterarySession,
                prepareCoreConcurrently,
                LogImageAnalysisRuntime,
                progress: null,
                cancellationToken);
            if (isHeavy
                && GetImageAnalysisLiteraryPipeline(_imageAnalysisLiterarySession)
                    is IHeavyResourceMonitoringPipeline heavyMonitor)
            {
                StartHeavyResourceMonitor(heavyMonitor);
                RefreshImageAnalysisSpeechUi();
            }
            LogImageAnalysisRuntime(RuntimeResourceDiagnostics.DescribeSystemMemory(
                "after_image_analysis_runtime_preparation"));
            if (ReferenceEquals(_imageAnalysisRuntimePreparationCts, owner)
                && ImageAnalysisWorkspacePage.Visibility == Visibility.Visible
                && _imageAnalysisLiteraryCts is null)
            {
                StatusText.Text = L("Status.ImageAnalysisModelsReady");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogImageAnalysisRuntime("Image-analysis model preparation was cancelled.");
        }
        catch (Exception ex)
        {
            LogImageAnalysisRuntime($"Image-analysis model preparation failed: {ex.Message}");
            if (isHeavy)
            {
                RefreshImageAnalysisSpeechUi();
            }
            if (ReferenceEquals(_imageAnalysisRuntimePreparationCts, owner)
                && ImageAnalysisWorkspacePage.Visibility == Visibility.Visible
                && _imageAnalysisLiteraryCts is null)
            {
                StatusText.Text = isHeavy
                    ? LF("Status.ImageAnalysisHeavyPreparationFailed", ex.Message)
                    : L("Status.ImageAnalysisModelsDeferred");
            }
        }
        finally
        {
            if (ReferenceEquals(_imageAnalysisRuntimePreparationCts, owner))
            {
                _imageAnalysisRuntimePreparationCts = null;
                _imageAnalysisRuntimePreparationTask = null;
                owner.Dispose();
            }
        }
    }

    private void CancelImageAnalysisRuntimePreparation(bool stopModels)
    {
        var preparationCts = _imageAnalysisRuntimePreparationCts;
        _imageAnalysisRuntimePreparationCts = null;
        _imageAnalysisRuntimePreparationTask = null;
        if (preparationCts is not null)
        {
            preparationCts.Cancel();
            preparationCts.Dispose();
        }

        if (stopModels)
        {
            StopHeavyResourceMonitor();
            _imageAnalysisLiteraryPipeline?.Stop();
        }
    }

    private void StartHeavyResourceMonitor(IHeavyResourceMonitoringPipeline pipeline)
    {
        StopHeavyResourceMonitor();
        _heavyResourceWarningShown = false;
        _heavyResourceMonitorCts = new CancellationTokenSource();
        _heavyResourceMonitorTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(15)
        };
        _heavyResourceMonitorTimer.Tick += async (_, _) =>
            await CaptureHeavyResourceStatusAsync(pipeline);
        _heavyResourceMonitorTimer.Start();
        LogImageAnalysisRuntime("Heavy post-warmup resource monitor started; placement remains fixed.");
    }

    private async Task CaptureHeavyResourceStatusAsync(IHeavyResourceMonitoringPipeline pipeline)
    {
        if (_heavyResourceMonitorBusy
            || _heavyResourceMonitorCts is null
            || _imageAnalysisLiteraryCts is not null
            || _imageAnalysisSpeechCts is not null)
        {
            return;
        }
        _heavyResourceMonitorBusy = true;
        try
        {
            var status = await pipeline.CaptureResourceStatusAsync(_heavyResourceMonitorCts.Token);
            LogImageAnalysisRuntime(
                $"Heavy resource monitor: ramFreeBytes={status.Sample.AvailableRamBytes}; " +
                $"commitFreeBytes={status.Sample.CommitAvailableBytes}; " +
                $"vramFreeBytes={status.Sample.AvailableVramBytes}; " +
                $"ramPressure={status.RamPressure}; commitPressure={status.CommitPressure}; " +
                $"vramPressure={status.VramPressure}; restartRecommended={status.RestartRecommended}.");
            if (!status.RestartRecommended || _heavyResourceWarningShown)
            {
                return;
            }
            _heavyResourceWarningShown = true;
            SaveCurrentImageAnalysisSession();
            StatusText.Text = L("Status.ImageAnalysisHeavyMemoryPressure");
            WpfMessageBox.Show(
                this,
                L("ImageAnalysis.Heavy.MemoryPressure.Message"),
                L("ImageAnalysis.Heavy.MemoryPressure.Title"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch (OperationCanceledException) when (_heavyResourceMonitorCts?.IsCancellationRequested != false)
        {
        }
        catch (Exception ex)
        {
            LogImageAnalysisRuntime($"Heavy resource monitor sample failed: {ex.Message}");
        }
        finally
        {
            _heavyResourceMonitorBusy = false;
        }
    }

    private void StopHeavyResourceMonitor()
    {
        _heavyResourceMonitorTimer?.Stop();
        _heavyResourceMonitorTimer = null;
        _heavyResourceMonitorCts?.Cancel();
        _heavyResourceMonitorCts?.Dispose();
        _heavyResourceMonitorCts = null;
        _heavyResourceMonitorBusy = false;
    }

    private string BuildImageAnalysisMarkdown(ImageAnalysisLiteraryVersion version) =>
        $"# {L("ImageAnalysis.Document.Title")}{Environment.NewLine}{Environment.NewLine}{version.Text.Trim()}";

    private void SaveCurrentImageAnalysisSession()
    {
        try
        {
            if (_imageAnalysisLiterarySession is not null)
            {
                _imageAnalysisSessionStore.Save(_imageAnalysisLiterarySession, _storageSettings);
            }
        }
        catch
        {
            // Best-effort save during navigation or application shutdown.
        }
    }

    private void CancelImageAnalysisLiteraryOperation()
    {
        _imageAnalysisLiteraryCts?.Cancel();
        _imageAnalysisLiteraryCts?.Dispose();
        _imageAnalysisLiteraryCts = null;
    }

    private void DisposeImageAnalysisLiteraryRuntime()
    {
        CancelImageAnalysisRuntimePreparation(stopModels: true);
        CancelImageAnalysisLiteraryOperation();
        SaveCurrentImageAnalysisSession();
        DisposeImageAnalysisSpeech();
        _imageAnalysisLiteraryPipeline?.Dispose();
        _imageAnalysisLiteraryPipeline = null;
    }

    private void LogImageAnalysisRuntime(string message)
    {
        try
        {
            _coreSessionLog?.Write("image_analysis_runtime", new
            {
                SessionId = _imageAnalysisLiterarySession?.SessionId,
                Message = message
            });
        }
        catch
        {
            // Runtime diagnostics must not break the user scenario.
        }
    }
}
