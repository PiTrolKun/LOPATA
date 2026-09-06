using System.Globalization;
using System.Windows;
using AIHub.Models;
using AIHub.Services;
using UserControl = System.Windows.Controls.UserControl;

namespace AIHub.Controls;

public partial class ImageAnalysisBundleConfirmationControl : UserControl
{
    private ImageAnalysisBundleDefinition? _bundle;
    private ImageAnalysisBundleInstallationSnapshot? _snapshot;
    private Func<string, string> _localize = key => key;
    private Func<string, object[], string> _format = (key, _) => key;
    private string _primaryAction = ImageAnalysisBundleActions.None;
    private ManagedModelDownloadProgress? _activeProgress;
    private bool _isBusy;
    private bool _hasHistory;

    public ImageAnalysisBundleConfirmationControl()
    {
        InitializeComponent();
    }

    public event EventHandler? BackToBundlesRequested;

    public event EventHandler? BackToWorkStartRequested;

    public event EventHandler<ImageAnalysisBundleActionEventArgs>? ActionRequested;

    public event EventHandler? RemoveVisionRequested;

    public event EventHandler? CancelRequested;

    public event EventHandler? ViewHistoryRequested;

    public void Configure(
        ImageAnalysisBundleDefinition bundle,
        ImageAnalysisBundleInstallationSnapshot snapshot,
        Func<string, string> localize,
        Func<string, object[], string> format,
        bool hasHistory = false)
    {
        _bundle = bundle;
        _snapshot = snapshot;
        _localize = localize;
        _format = format;
        _hasHistory = hasHistory;
        ApplyLocalization();
    }

    public void UpdateSnapshot(ImageAnalysisBundleInstallationSnapshot snapshot)
    {
        _activeProgress = null;
        _snapshot = snapshot;
        ApplyLocalization();
    }

    public void SetBusy(bool isBusy)
    {
        _isBusy = isBusy;
        PrimaryActionButton.IsEnabled = !isBusy;
        RemoveVisionButton.IsEnabled = !isBusy && _snapshot?.CanRemoveVision == true;
        BackToBundlesButton.IsEnabled = !isBusy;
        BackToWorkStartButton.IsEnabled = !isBusy;
        ViewHistoryButton.IsEnabled = !isBusy;
        ProgressPanel.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        CancelOperationButton.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        if (!isBusy)
        {
            _activeProgress = null;
            ApplyLocalization();
        }
    }

    public void UpdateProgress(ManagedModelDownloadProgress progress)
    {
        _activeProgress = progress;
        var isRuntimeStage = IsRuntimeStage(progress.Stage);
        ProgressPanel.Visibility = Visibility.Visible;
        ErrorDetailsText.Visibility = Visibility.Collapsed;
        StateTitleText.Text = progress.Stage == "downloading"
            ? _localize("ImageAnalysis.Install.Active.Title")
            : _localize("ImageAnalysis.Install.State.checking.Title");
        StateDescriptionText.Text = progress.Stage switch
        {
            "downloading" => _format("ImageAnalysis.Install.Active.Description", [progress.FileName]),
            _ when isRuntimeStage => _format("ImageAnalysis.Install.Runtime.ActiveDescription", [progress.FileName]),
            _ => _localize("ImageAnalysis.Install.State.checking.Description")
        };
        RefreshComponentItems(progress);
        OperationProgressBar.IsIndeterminate = progress.TotalBytes <= 0;
        OperationProgressBar.Value = progress.TotalBytes <= 0
            ? 0
            : Math.Clamp(progress.DownloadedBytes * 100d / progress.TotalBytes, 0, 100);
        ProgressStageText.Text = _format(
            "ImageAnalysis.Install.ProgressStage",
            [LocalizeStage(progress.Stage), progress.FileName]);
        ProgressValueText.Text = BuildProgressValue(progress);
        ProgressHintText.Text = _localize("ImageAnalysis.Install.Verification.DoNotClose");
        ProgressHintText.Visibility = progress.Stage == "downloading"
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    public void ApplyLocalization()
    {
        if (_bundle is null || _snapshot is null)
        {
            return;
        }
        var bundleTitle = _localize(_bundle.TitleKey);
        var isHeavy = ImageAnalysisModeCapabilities.UsesOmniConversation(_bundle.Id);
        TitleText.Text = _format("ImageAnalysis.Confirmation.Title", [bundleTitle.ToUpper(CultureInfo.CurrentUICulture)]);
        ModeSymbolText.Text = _bundle.Id switch { "light" => "α", "medium" => "β", "heavy" => "γ", _ => string.Empty };
        DescriptionText.Visibility = isHeavy ? Visibility.Collapsed : Visibility.Visible;
        DescriptionText.Text = _localize(isHeavy
            ? "ImageAnalysis.Heavy.StartNotice"
            : "ImageAnalysis.Install.Description");
        StateTitleText.Text = isHeavy && _snapshot.State == ImageAnalysisBundleInstallStates.Ready
            ? _localize(_bundle.Id == ImageAnalysisBundleCatalog.LightId ? "ImageAnalysis.Install.Alpha.ReadyTitle" : "ImageAnalysis.Install.State.heavy_ready.Title")
            : _localize($"ImageAnalysis.Install.State.{_snapshot.State}.Title");
        StateDescriptionText.Text = isHeavy && _snapshot.State == ImageAnalysisBundleInstallStates.Ready
            ? _localize(_bundle.Id == ImageAnalysisBundleCatalog.LightId ? "ImageAnalysis.Install.Alpha.ReadyDescription" : "ImageAnalysis.Install.State.heavy_ready.Description")
            : BuildStateDescription(_snapshot);
        UpdateErrorDetails();
        RefreshComponentItems();
        ModelsPathText.Text = string.IsNullOrWhiteSpace(_snapshot.ModelsRoot)
            ? _localize("ImageAnalysis.Install.StorageMissing")
            : isHeavy
                ? _format(
                    "ImageAnalysis.Install.StoragePathWithFree",
                    [_snapshot.ModelsRoot, FormatBytes(_snapshot.AvailableFreeBytes)])
                : _format("ImageAnalysis.Install.StoragePath", [_snapshot.ModelsRoot]);
        ConfigurePrimaryAction(_snapshot.State);
        ViewHistoryButton.Content = _localize("ImageAnalysis.Install.ViewHistoryReadOnly");
        ViewHistoryButton.Visibility = isHeavy && !_snapshot.CanStart && _hasHistory
            ? Visibility.Visible
            : Visibility.Collapsed;
        RemoveVisionButton.ToolTip = _localize("ImageAnalysis.Install.RemoveVision");
        System.Windows.Automation.AutomationProperties.SetName(RemoveVisionButton, _localize("ImageAnalysis.Install.RemoveVision"));
        RemoveVisionButton.IsEnabled = _snapshot.CanRemoveVision;
        RemoveVisionButton.Visibility = _snapshot.CanRemoveVision ? Visibility.Visible : Visibility.Collapsed;
        CancelOperationButton.Content = _localize("Common.Cancel");
        BackToBundlesButton.Content = _localize("ImageAnalysis.Confirmation.BackToBundles");
        BackToWorkStartButton.Content = _localize("ImageAnalysis.Confirmation.BackToStart");
        if (_activeProgress is not null)
        {
            UpdateProgress(_activeProgress);
        }
        if (_isBusy)
        {
            PrimaryActionButton.IsEnabled = false;
            RemoveVisionButton.IsEnabled = false;
            BackToBundlesButton.IsEnabled = false;
            BackToWorkStartButton.IsEnabled = false;
            ViewHistoryButton.IsEnabled = false;
        }
    }

    private string BuildStateDescription(ImageAnalysisBundleInstallationSnapshot snapshot)
    {
        var key = $"ImageAnalysis.Install.State.{snapshot.State}.Description";
        return snapshot.State is ImageAnalysisBundleInstallStates.DownloadRequired
            or ImageAnalysisBundleInstallStates.ResumeAvailable
            ? _format(key, [FormatBytes(snapshot.MissingBytes)])
            : _localize(key);
    }

    private void ConfigurePrimaryAction(string state)
    {
        (_primaryAction, PrimaryActionButton.Content, PrimaryActionButton.IsEnabled) = state switch
        {
            ImageAnalysisBundleInstallStates.DownloadRequired => (ImageAnalysisBundleActions.Download, _localize("ImageAnalysis.Install.DownloadMissing"), true),
            ImageAnalysisBundleInstallStates.ResumeAvailable => (ImageAnalysisBundleActions.Download, _localize("ImageAnalysis.Install.Resume"), true),
            ImageAnalysisBundleInstallStates.Corrupted => (ImageAnalysisBundleActions.Download, _localize("ImageAnalysis.Install.Repair"), true),
            ImageAnalysisBundleInstallStates.NeedsVerification => (ImageAnalysisBundleActions.Verify, _localize("ImageAnalysis.Install.Verify"), true),
            ImageAnalysisBundleInstallStates.RuntimeIncompatible => (ImageAnalysisBundleActions.Verify, _localize("ImageAnalysis.Install.VerifyAgain"), true),
            ImageAnalysisBundleInstallStates.Ready => (ImageAnalysisBundleActions.Start, _localize("ImageAnalysis.Install.Start"), true),
            _ => (ImageAnalysisBundleActions.None, _localize("ImageAnalysis.Install.Unavailable"), false)
        };
    }

    private string LocalizeModelStatus(string status) => _localize($"Models.Status.{status}");

    private string LocalizeRole(string role) => _localize($"Models.Role.{role}");

    private string LocalizeStage(string stage) => _localize($"Models.Stage.{stage}");

    private void RefreshComponentItems(ManagedModelDownloadProgress? activeProgress = null)
    {
        if (_snapshot is null)
        {
            return;
        }
        var effectiveProgress = activeProgress ?? _activeProgress;
        ComponentItemsControl.ItemsSource = _snapshot.Components.Select(component =>
        {
            var isActive = string.Equals(
                component.ModelArtifactId,
                effectiveProgress?.ModelArtifactId,
                StringComparison.Ordinal);
            var storedBytes = isActive && effectiveProgress is not null
                ? Math.Max(component.StoredBytes, effectiveProgress.DownloadedBytes)
                : component.StoredBytes;
            return new ImageAnalysisInstallComponentViewModel
            {
                DisplayName = component.DisplayName,
                StatusText = isActive && effectiveProgress is not null
                    ? effectiveProgress.Stage == "downloading"
                        ? _localize("ImageAnalysis.Install.Active.ComponentStatus")
                        : LocalizeStage(effectiveProgress.Stage)
                    : LocalizeModelStatus(component.Status),
                DetailText = BuildComponentDetail(component, storedBytes)
            };
        }).ToList();
    }

    private string BuildComponentDetail(ImageAnalysisBundleComponentState component, long storedBytes)
    {
        if (ImageAnalysisModeCapabilities.UsesOmniConversation(_bundle?.Id))
        {
            return _format(
                "ImageAnalysis.Install.Component.HeavyProfile",
                [
                    LocalizeRole(component.Role),
                    FormatBytes(storedBytes),
                    FormatBytes(component.TotalBytes),
                    component.RepositoryId,
                    component.Revision,
                    component.License
                ]);
        }

        return _format(
            component.IsShared ? "ImageAnalysis.Install.Component.Shared" : "ImageAnalysis.Install.Component.Profile",
            [LocalizeRole(component.Role), FormatBytes(storedBytes), FormatBytes(component.TotalBytes)]);
    }

    private string BuildProgressValue(ManagedModelDownloadProgress progress)
    {
        if (IsRuntimeStage(progress.Stage))
        {
            return progress.Stage == "runtime_verified"
                ? _localize("ImageAnalysis.Install.Runtime.Completed")
                : _localize("ImageAnalysis.Install.Runtime.Wait");
        }
        if (progress.TotalBytes <= 0)
        {
            return FormatBytes(progress.DownloadedBytes);
        }
        var downloaded = FormatBytes(progress.DownloadedBytes);
        var total = FormatBytes(progress.TotalBytes);
        var percent = OperationProgressBar.Value.ToString("0", CultureInfo.CurrentCulture);
        if (progress.Stage != "downloading" || progress.BytesPerSecond <= 0)
        {
            return _format("ImageAnalysis.Install.ProgressValue", [downloaded, total, percent]);
        }
        var remainingSeconds = Math.Max(0, progress.TotalBytes - progress.DownloadedBytes) / progress.BytesPerSecond;
        var remaining = FormatRemainingTime(TimeSpan.FromSeconds(remainingSeconds));
        return _format(
            "ImageAnalysis.Install.ProgressValueWithSpeed",
            [downloaded, total, percent, FormatBytes((long)progress.BytesPerSecond), remaining]);
    }

    private void UpdateErrorDetails()
    {
        var error = _snapshot?.Components
            .Select(component => component.LastError)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        ErrorDetailsText.Text = string.IsNullOrWhiteSpace(error)
            ? string.Empty
            : _format("ImageAnalysis.Install.ErrorDetails", [error]);
        ErrorDetailsText.Visibility = string.IsNullOrWhiteSpace(error)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private static bool IsRuntimeStage(string stage) =>
        stage is "runtime_loading" or "runtime_verified";

    private string FormatRemainingTime(TimeSpan remaining)
    {
        if (remaining < TimeSpan.FromMinutes(1))
        {
            return _localize("ImageAnalysis.Install.Remaining.LessMinute");
        }
        if (remaining < TimeSpan.FromHours(1))
        {
            return _format("ImageAnalysis.Install.Remaining.Minutes", [Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))]);
        }
        return _format(
            "ImageAnalysis.Install.Remaining.HoursMinutes",
            [(int)remaining.TotalHours, remaining.Minutes]);
    }

    private static string FormatBytes(long bytes) => ComponentCardViewModel.FormatBytes(bytes);

    private void PrimaryActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_primaryAction != ImageAnalysisBundleActions.None)
        {
            ActionRequested?.Invoke(this, new ImageAnalysisBundleActionEventArgs(_primaryAction));
        }
    }

    private void RemoveVisionButton_Click(object sender, RoutedEventArgs e) =>
        RemoveVisionRequested?.Invoke(this, EventArgs.Empty);

    private void CancelOperationButton_Click(object sender, RoutedEventArgs e) =>
        CancelRequested?.Invoke(this, EventArgs.Empty);

    private void ViewHistoryButton_Click(object sender, RoutedEventArgs e) =>
        ViewHistoryRequested?.Invoke(this, EventArgs.Empty);

    private void BackToBundlesButton_Click(object sender, RoutedEventArgs e) =>
        BackToBundlesRequested?.Invoke(this, EventArgs.Empty);

    private void BackToWorkStartButton_Click(object sender, RoutedEventArgs e) =>
        BackToWorkStartRequested?.Invoke(this, EventArgs.Empty);
}

public static class ImageAnalysisBundleActions
{
    public const string None = "none";
    public const string Download = "download";
    public const string Verify = "verify";
    public const string Start = "start";
}

public sealed class ImageAnalysisBundleActionEventArgs(string action) : EventArgs
{
    public string Action { get; } = action;
}

public sealed class ImageAnalysisInstallComponentViewModel
{
    public string DisplayName { get; init; } = string.Empty;

    public string StatusText { get; init; } = string.Empty;

    public string DetailText { get; init; } = string.Empty;
}
