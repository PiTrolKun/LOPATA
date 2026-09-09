using System.Windows;
using System.IO;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;
using WpfMessageBox = System.Windows.MessageBox;

namespace AIHub;

public partial class MainWindow
{
    private readonly ImageAnalysisRecommendationService _imageAnalysisRecommendationService = new();
    private readonly ImageAnalysisHardwareSnapshotService _imageAnalysisHardwareSnapshotService = new();
    private readonly ImageAnalysisBundleInstallationService _imageAnalysisBundleInstallationService = new();
    private IReadOnlyList<ImageAnalysisBundleDefinition> _imageAnalysisBundles = [];
    private ImageAnalysisRecommendationResult? _imageAnalysisRecommendation;
    private ImageAnalysisBundleDefinition? _selectedImageAnalysisBundle;
    private CancellationTokenSource? _imageAnalysisBundleOperationCts;
    private readonly ImageAnalysisFileValidationService _imageAnalysisFileValidationService = new();
    private readonly ImageAnalysisSessionStore _imageAnalysisSessionStore = new();
    private ISingleImageLiteraryPipeline? _imageAnalysisLiteraryPipeline;
    private ImageAnalysisLiterarySession? _imageAnalysisLiterarySession;
    private CancellationTokenSource? _imageAnalysisLiteraryCts;
    private CancellationTokenSource? _imageAnalysisRuntimePreparationCts;
    private Task? _imageAnalysisRuntimePreparationTask;
    private bool _imageAnalysisWorkspaceReadOnly;

    private void SelectImageAnalysisButton_Click(object sender, RoutedEventArgs e)
    {
        ShowImageAnalysisBundleSelector(refreshHardware: true);
        StatusText.Text = L("Status.ImageAnalysisOpened");
    }

    private void ShowImageAnalysisBundleSelector(bool refreshHardware)
    {
        CancelCoreSpeech(revealFullText: false, "open_image_analysis_selector");
        if (refreshHardware || _imageAnalysisRecommendation is null)
        {
            _lastPassport = _computerPassportService.EnsurePassport();
            _imageAnalysisBundles = ImageAnalysisBundleCatalog.Create();
            var hardware = _imageAnalysisHardwareSnapshotService.Create(
                _lastPassport,
                _storageSettings);
            _imageAnalysisRecommendation = _imageAnalysisRecommendationService.Evaluate(
                _imageAnalysisBundles,
                hardware);
            ImageAnalysisBundleSelectorPage.Configure(
                _imageAnalysisBundles,
                _imageAnalysisRecommendation,
                L,
                LF);
        }

        if (!HideStandardPages()) return;
        ImageAnalysisBundleConfirmationPage.Visibility = Visibility.Collapsed;
        ImageAnalysisBundleSelectorPage.Visibility = Visibility.Visible;
        ImageAnalysisBundleSelectorPage.UpdateResponsiveLayout(ActualWidth);
    }

    private void ShowImageAnalysisBundleConfirmation(ImageAnalysisBundleDefinition bundle)
    {
        _selectedImageAnalysisBundle = bundle;
        var snapshot = _imageAnalysisBundleInstallationService.Check(_storageSettings, bundle.Id);
        var hasHistory = _imageAnalysisSessionStore.LoadAll(_storageSettings)
            .Any(session => string.Equals(session.BundleId, bundle.Id, StringComparison.Ordinal));
        ImageAnalysisBundleConfirmationPage.Configure(bundle, snapshot, L, LF, hasHistory);
        if (!HideStandardPages()) return;
        ImageAnalysisBundleSelectorPage.Visibility = Visibility.Collapsed;
        EndImageInputSession();
        ImageAnalysisWorkspacePage.Visibility = Visibility.Collapsed;
        ImageAnalysisBundleConfirmationPage.Visibility = Visibility.Visible;
    }

    private bool HideStandardPages()
    {
        if (LiteraryPage.IsVisible && !LiteraryPage.CanLeave()) return false;
        LiteraryPage.Visibility = Visibility.Collapsed;
        CoreMemoryIndicatorPanel.Visibility = Visibility.Collapsed;
        WelcomePage.Visibility = Visibility.Collapsed;
        SetupPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;
        ProfilePage.Visibility = Visibility.Collapsed;
        ProfileReminderPage.Visibility = Visibility.Collapsed;
        WorkStartPage.Visibility = Visibility.Collapsed;
        ChoiceScenarioPage.Visibility = Visibility.Collapsed;
        return true;
    }

    private bool HideImageAnalysisPages()
    {
        if (LiteraryPage.IsVisible && !LiteraryPage.CanLeave()) return false;
        LiteraryPage.Visibility = Visibility.Collapsed;
        CoreMemoryIndicatorPanel.Visibility = Visibility.Visible;
        ImageAnalysisBundleSelectorPage.Visibility = Visibility.Collapsed;
        ImageAnalysisBundleConfirmationPage.Visibility = Visibility.Collapsed;
        EndImageInputSession();
        ImageAnalysisWorkspacePage.Visibility = Visibility.Collapsed;
        return true;
    }

    private void RefreshImageAnalysisLocalization()
    {
        OpenImagesFolderButton.Content = L("ImageInput.Folder");
        if (_imageAnalysisLiteraryCts is not null
            && _imageAnalysisLiterarySession is not null
            && ImageAnalysisModeCapabilities.UsesOmniConversation(_imageAnalysisLiterarySession.BundleId)
            && !string.Equals(
                _imageAnalysisLiterarySession.AnalysisLanguageCode,
                NormalizeSpeechLanguage(_appSettings.LanguageCode),
                StringComparison.Ordinal))
        {
            _restartHeavyAnalysisAfterLanguageChange = true;
            _imageAnalysisLiteraryCts.Cancel();
        }
        ImageAnalysisScenarioTitleText.Text = L("ImageAnalysis.Scenario.Title");
        ImageAnalysisScenarioDescriptionText.Text = L("ImageAnalysis.Scenario.Description");
        SelectImageAnalysisButton.Content = L("ImageAnalysis.Scenario.Select");

        if (_imageAnalysisRecommendation is not null)
        {
            ImageAnalysisBundleSelectorPage.ApplyLocalization();
        }

        if (_selectedImageAnalysisBundle is not null)
        {
            ImageAnalysisBundleConfirmationPage.ApplyLocalization();
        }

        ImageAnalysisWorkspacePage.ApplyLocalization();
        RefreshImageAnalysisSpeechUi();
        if (ImageAnalysisWorkspacePage.Visibility == Visibility.Visible)
        {
            ImageAnalysisWorkspacePage.SetReadOnlyMode(_imageAnalysisWorkspaceReadOnly);
        }
    }

    private bool TryHandleImageAnalysisEscape()
    {
        if (ImageAnalysisWorkspacePage.Visibility == Visibility.Visible)
        {
            if (_imageAnalysisLiteraryCts is not null)
            {
                _imageAnalysisLiteraryCts.Cancel();
                return true;
            }
            SaveCurrentImageAnalysisSession();
            CancelImageAnalysisRuntimePreparation(stopModels: true);
            StopImageAnalysisSpeechSession();
            if (_selectedImageAnalysisBundle is not null)
            {
                ShowImageAnalysisBundleConfirmation(_selectedImageAnalysisBundle);
            }
            return true;
        }

        if (ImageAnalysisBundleConfirmationPage.Visibility == Visibility.Visible)
        {
            ShowImageAnalysisBundleSelector(refreshHardware: false);
            StatusText.Text = L("Status.ImageAnalysisOpened");
            return true;
        }

        if (ImageAnalysisBundleSelectorPage.Visibility == Visibility.Visible)
        {
            ShowWorkStartPage();
            StatusText.Text = L("Status.WorkStartOpened");
            return true;
        }

        return false;
    }

    private void ImageAnalysisBundleSelectorPage_BackRequested(object? sender, EventArgs e)
    {
        ShowWorkStartPage();
        StatusText.Text = L("Status.WorkStartOpened");
    }

    private void ImageAnalysisBundleSelectorPage_BundleSelected(
        object? sender,
        ImageAnalysisBundleSelectedEventArgs e)
    {
        ShowImageAnalysisBundleConfirmation(e.Bundle);
        StatusText.Text = LF("Status.ImageAnalysisBundleSelected", L(e.Bundle.TitleKey));
    }

    private void ImageAnalysisBundleSelectorPage_UnavailableBundleRequested(
        object? sender,
        ImageAnalysisBundleSelectedEventArgs e)
    {
        StatusText.Text = LF("Status.ImageAnalysisBundleUnavailable", L(e.Bundle.TitleKey));
    }

    private void ImageAnalysisBundleConfirmationPage_BackToBundlesRequested(
        object? sender,
        EventArgs e)
    {
        ShowImageAnalysisBundleSelector(refreshHardware: false);
        StatusText.Text = L("Status.ImageAnalysisOpened");
    }

    private void ImageAnalysisBundleConfirmationPage_BackToWorkStartRequested(
        object? sender,
        EventArgs e)
    {
        ShowWorkStartPage();
        StatusText.Text = L("Status.WorkStartOpened");
    }

    private async void ImageAnalysisBundleConfirmationPage_ActionRequested(
        object? sender,
        ImageAnalysisBundleActionEventArgs e)
    {
        if (e.Action == ImageAnalysisBundleActions.Start)
        {
            ShowImageAnalysisWorkspace();
            if (ImageAnalysisWorkspacePage.Visibility == Visibility.Visible)
            {
                BeginImageAnalysisRuntimePreparation();
            }
            return;
        }

        var bundleId = _selectedImageAnalysisBundle?.Id ?? ImageAnalysisBundleCatalog.MediumId;
        var snapshot = _imageAnalysisBundleInstallationService.Check(_storageSettings, bundleId);
        if (e.Action == ImageAnalysisBundleActions.Download)
        {
            var names = string.Join(
                Environment.NewLine,
                snapshot.Components
                    .Where(component => component.Status != ManagedModelStatuses.Installed
                        && component.Status != ManagedModelStatuses.NeedsVerification)
                    .Select(component => $"• {component.DisplayName}"));
            var confirmation = WpfMessageBox.Show(
                this,
                LF(
                    "ImageAnalysis.Install.DownloadConfirm",
                    names,
                    ComponentCardViewModel.FormatBytes(snapshot.MissingBytes),
                    snapshot.ModelsRoot),
                L(bundleId == ImageAnalysisBundleCatalog.LightId ? "ImageAnalysis.Install.Alpha.DownloadTitle" : bundleId == ImageAnalysisBundleCatalog.MediumId ? "ImageAnalysis.Install.Beta.DownloadTitle" : ImageAnalysisModeCapabilities.UsesOmniConversation(bundleId)
                    ? "ImageAnalysis.Install.HeavyDownloadTitle"
                    : "ImageAnalysis.Install.DownloadTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            if (confirmation != MessageBoxResult.Yes)
            {
                return;
            }
        }
        else if (e.Action == ImageAnalysisBundleActions.Verify)
        {
            var confirmation = WpfMessageBox.Show(
                this,
                L(ImageAnalysisModeCapabilities.UsesOmniConversation(bundleId)
                    ? "ImageAnalysis.Install.HeavyVerifyConfirm"
                    : "ImageAnalysis.Install.RuntimeConfirm"),
                L(ImageAnalysisModeCapabilities.UsesOmniConversation(bundleId)
                    ? "ImageAnalysis.Install.HeavyVerifyTitle"
                    : "ImageAnalysis.Install.VerifyTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            if (confirmation != MessageBoxResult.Yes)
            {
                return;
            }
        }

        _imageAnalysisBundleOperationCts?.Cancel();
        _imageAnalysisBundleOperationCts?.Dispose();
        _imageAnalysisBundleOperationCts = new CancellationTokenSource();
        ImageAnalysisBundleConfirmationPage.SetBusy(true);
        var progress = new Progress<ManagedModelDownloadProgress>(value =>
        {
            ImageAnalysisBundleConfirmationPage.UpdateProgress(value);
            StatusText.Text = LF("Status.ImageAnalysisModelOperation", value.FileName);
        });
        try
        {
            var updated = e.Action == ImageAnalysisBundleActions.Download
                ? await _imageAnalysisBundleInstallationService.DownloadMissingAsync(
                    _storageSettings,
                    progress,
                    _imageAnalysisBundleOperationCts.Token,
                    bundleId)
                : await _imageAnalysisBundleInstallationService.VerifyAsync(
                    _storageSettings,
                    progress,
                    _imageAnalysisBundleOperationCts.Token,
                    bundleId);
            ImageAnalysisBundleConfirmationPage.UpdateSnapshot(updated);
            StatusText.Text = updated.CanStart
                ? L(bundleId == ImageAnalysisBundleCatalog.LightId ? "Status.ImageAnalysisAlphaBundleReady" : bundleId == ImageAnalysisBundleCatalog.MediumId ? "Status.ImageAnalysisBetaBundleReady" : ImageAnalysisModeCapabilities.UsesOmniConversation(bundleId)
                    ? "Status.ImageAnalysisHeavyBundleReady"
                    : "Status.ImageAnalysisBundleReady")
                : L("Status.ImageAnalysisBundleUpdated");
            if (updated.CanStart
                && ImageAnalysisModeCapabilities.UsesOmniConversation(bundleId)
                && e.Action is ImageAnalysisBundleActions.Download
                    or ImageAnalysisBundleActions.Verify)
            {
                ShowImageAnalysisWorkspace();
                if (ImageAnalysisWorkspacePage.Visibility == Visibility.Visible)
                {
                    BeginImageAnalysisRuntimePreparation();
                }
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = L("Status.ImageAnalysisOperationCancelled");
        }
        catch (Exception)
        {
            var updated = _imageAnalysisBundleInstallationService.Check(_storageSettings, bundleId);
            ImageAnalysisBundleConfirmationPage.UpdateSnapshot(updated);
            StatusText.Text = L("Status.ImageAnalysisOperationFailed");
        }
        finally
        {
            ImageAnalysisBundleConfirmationPage.SetBusy(false);
        }
    }

    private void ImageAnalysisBundleConfirmationPage_RemoveVisionRequested(object? sender, EventArgs e)
    {
        var bundleId = _selectedImageAnalysisBundle?.Id ?? ImageAnalysisBundleCatalog.MediumId;
        var artifactId = ImageAnalysisModeCapabilities.VisionArtifact(bundleId);
        var card = _imageAnalysisBundleInstallationService.LibraryStore.Load(artifactId);
        if (card is null)
        {
            return;
        }
        var confirmation = new AIHub.Controls.ModelRemovalConfirmationDialog(
            this,
            L("ImageAnalysis.Install.RemoveVisionTitle"),
            LF("ImageAnalysis.Install.RemoveModelConfirm", card.DisplayName,
                ComponentCardViewModel.FormatBytes(card.StoredBytes)),
            L("Common.Cancel"),
            L("ImageAnalysis.Install.ConfirmRemove"));
        if (confirmation.ShowDialog() != true)
        {
            return;
        }
        try
        {
            var result = _imageAnalysisBundleInstallationService.RemoveVisionFiles(bundleId);
            ImageAnalysisBundleConfirmationPage.UpdateSnapshot(
                _imageAnalysisBundleInstallationService.Check(_storageSettings, bundleId));
            StatusText.Text = LF(
                "Status.ImageAnalysisVisionRemoved",
                ComponentCardViewModel.FormatBytes(result.RemovedBytes));
        }
        catch (Exception)
        {
            StatusText.Text = L("Status.ImageAnalysisVisionRemoveFailed");
        }
    }

    private void ImageAnalysisBundleConfirmationPage_CancelRequested(object? sender, EventArgs e) =>
        _imageAnalysisBundleOperationCts?.Cancel();

    private void ImageAnalysisBundleConfirmationPage_ViewHistoryRequested(object? sender, EventArgs e)
    {
        ShowImageAnalysisWorkspace(allowUnavailableReadOnly: true);
    }

    private void ShowImageAnalysisWorkspace(bool allowUnavailableReadOnly = false)
    {
        var bundleId = _selectedImageAnalysisBundle?.Id ?? ImageAnalysisBundleCatalog.MediumId;
        var snapshot = _imageAnalysisBundleInstallationService.Check(_storageSettings, bundleId);
        if (!snapshot.CanStart && !allowUnavailableReadOnly)
        {
            ImageAnalysisBundleConfirmationPage.UpdateSnapshot(snapshot);
            return;
        }
        _imageAnalysisWorkspaceReadOnly = !snapshot.CanStart;
        ImageAnalysisWorkspacePage.Configure(bundleId, L, LF, () => _appSettings.LanguageCode);
        ImageAnalysisWorkspacePage.SetReadOnlyMode(_imageAnalysisWorkspaceReadOnly);
        if (!HideStandardPages()) return;
        ImageAnalysisBundleSelectorPage.Visibility = Visibility.Collapsed;
        ImageAnalysisBundleConfirmationPage.Visibility = Visibility.Collapsed;
        ImageAnalysisWorkspacePage.Visibility = Visibility.Visible;
        ShowImageAnalysisSubscenarioSelection();
        RefreshImageAnalysisSpeechUi();
        ImageAnalysisWorkspacePage.SetReadOnlyMode(_imageAnalysisWorkspaceReadOnly);
        StatusText.Text = L(_imageAnalysisWorkspaceReadOnly
            ? "Status.ImageAnalysisHistoryReadOnly"
            : "Status.ImageAnalysisWorkspaceOpened");
    }

    private void ImageAnalysisWorkspacePage_BackRequested(object? sender, EventArgs e)
    {
        if (ImageAnalysisWorkspacePage.IsBatch)
        {
            if (_batchCts is not null || _batchImporting) return;
            if (ImageAnalysisWorkspacePage.IsBatchSettings && _batchJob is not null)
            {
                _batchJob.Settings = ImageAnalysisWorkspacePage.BatchSettings;
                _batchJob.Settings.LanguageCode = _appSettings.LanguageCode;
                _batchJob.SingleDocument = ImageAnalysisWorkspacePage.BatchSingleDocument;
                BatchStore.Save(_batchJob);
                _batchView!.Files(_batchJob); ImageAnalysisWorkspacePage.ShowBatchContent(_batchView); return;
            }
            if (_batchJob is not null) { ShowBatchSelector(); return; }
            CloseImageBatch(); ShowImageAnalysisSubscenarioSelection(); return;
        }
        CancelImageAnalysisLiteraryOperation();
        CancelImageAnalysisRuntimePreparation(stopModels: true);
        StopImageAnalysisSpeechSession();
        SaveCurrentImageAnalysisSession();
        if (_selectedImageAnalysisBundle is not null)
        {
            ShowImageAnalysisBundleConfirmation(_selectedImageAnalysisBundle);
        }
    }

}
