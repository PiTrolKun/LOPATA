using System.Windows;
using System.IO;
using AIHub.Models;
using AIHub.Services;
using WpfButton = System.Windows.Controls.Button;
using WpfMessageBox = System.Windows.MessageBox;

namespace AIHub;

public partial class MainWindow
{
    private ManagedModelInventoryService? _managedModelInventory;
    private ManagedModelAcquisitionService? _managedModelAcquisition;
    private ManagedModelRemovalService? _managedModelRemoval;
    private ImageAnalysisRuntimeCompatibilityService? _managedModelRuntimeCompatibility;
    private IReadOnlyList<ManagedModelArtifactCard> _managedModelLibrary = [];
    private bool _managedModelsInitialized;
    private bool _managedModelOperationActive;
    private CancellationTokenSource? _managedModelOperationCts;

    private void InitializeManagedModelsUi()
    {
        var store = _imageAnalysisBundleInstallationService.LibraryStore;
        _managedModelInventory = new ManagedModelInventoryService(store, _componentManager);
        _managedModelAcquisition = new ManagedModelAcquisitionService(store);
        _managedModelRemoval = new ManagedModelRemovalService(
            store,
            new DelegateModelUsageGuard(IsManagedModelActive));
        _managedModelRuntimeCompatibility = new ImageAnalysisRuntimeCompatibilityService(store);
        ApplyModelDownloadSettings();
        PopulateManagedModelFilter();
        RefreshManagedModelLocalization();
    }

    private void ApplyModelDownloadSettings()
    {
        var connectionCount = _appSettings.ModelDownloads?.MaximumParallelConnections ?? 0;
        if (_managedModelAcquisition is not null)
        {
            _managedModelAcquisition.MaximumParallelConnections = connectionCount;
        }
        _imageAnalysisBundleInstallationService.MaximumParallelConnections = connectionCount;
        GenerationPage.DownloadConnections(connectionCount);
    }

    private void RegisterPendingSandboxExecutorArtifact(
        ExecutorModelArtifact artifact,
        ChoiceTaskCard taskCard)
    {
        try
        {
            var modelsRoot = _storageSettings.Models.Locations
                .Select(location => location.Path?.Trim())
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
            if (string.IsNullOrWhiteSpace(modelsRoot))
            {
                return;
            }
            var revision = ParseRevision(artifact.DownloadUrl);
            var directoryName = string.Concat(artifact.RepoId.Select(character =>
                Path.GetInvalidFileNameChars().Contains(character) || character is '/' or '\\' ? '_' : character)).Trim('_');
            var card = new ManagedModelArtifactCard
            {
                Family = artifact.RequestedModel,
                DisplayName = string.IsNullOrWhiteSpace(artifact.RepoId) ? artifact.RequestedModel : artifact.RepoId,
                Role = ManagedModelRoles.Executor,
                Provider = "Hugging Face",
                RepositoryId = artifact.RepoId,
                Revision = revision,
                Format = "GGUF",
                Architecture = artifact.Architecture,
                Quantization = artifact.Quantization,
                License = artifact.License,
                SourcePage = $"https://huggingface.co/{artifact.RepoId}",
                IsManaged = true,
                CanRemoveFiles = true,
                SupportsDirectDownload = !string.IsNullOrWhiteSpace(artifact.Sha256)
                    && IsImmutableModelRevision(revision),
                ModelsRoot = modelsRoot,
                InstallDirectory = Path.Combine(modelsRoot, "Executors", string.IsNullOrWhiteSpace(directoryName) ? "executor" : directoryName),
                Consumers =
                [
                    new ManagedModelConsumer { Id = "sandbox", DisplayName = L("ChoiceScenario.Title"), Kind = "scenario" }
                ],
                Files =
                [
                    new ManagedModelArtifactFile
                    {
                        RelativePath = artifact.FileName,
                        SourceUrl = artifact.DownloadUrl,
                        SizeBytes = artifact.SizeBytes,
                        Sha256 = artifact.Sha256,
                        Purpose = "executor_model"
                    }
                ]
            };
            card.ModelArtifactId = ManagedModelLibraryStore.CreateStableId(card);
            _imageAnalysisBundleInstallationService.LibraryStore.RegisterDynamicArtifact(
                card,
                "sandbox",
                _activeResumableSession?.SessionId ?? string.Empty,
                $"recommended={taskCard.RecommendedExecutor}; area={taskCard.Area}; workload={_userProfile.WorkloadMode}",
                "The Sandbox route resolved and the user confirmed this exact artifact.",
                GetAppVersion());
        }
        catch
        {
            // Library registration must not replace the existing, recoverable executor download flow.
        }
    }

    private bool IsManagedModelActive(string modelArtifactId)
    {
        if (GenerationPage.UsesArtifact(modelArtifactId)) return true;
        if (modelArtifactId == ManagedModelCatalog.CoreArtifactId)
        {
            return _choiceScenarioRuntimeService is not null || _executorWorkflowService.HasActiveSession;
        }
        var card = _imageAnalysisBundleInstallationService.LibraryStore.Load(modelArtifactId);
        if (card?.Role == ManagedModelRoles.Executor && _executorWorkflowService.HasActiveSession)
        {
            return true;
        }
        return modelArtifactId == ManagedModelCatalog.KimiMediumArtifactId
            && _imageAnalysisBundleOperationCts is { IsCancellationRequested: false };
    }

    private static bool IsImmutableModelRevision(string revision) =>
        revision.Length == 40 && revision.All(Uri.IsHexDigit);

    private void ManagedModelsExpander_Expanded(object sender, RoutedEventArgs e)
    {
        if (_managedModelsInitialized)
        {
            return;
        }
        RefreshManagedModels();
    }

    private void RefreshManagedModelsButton_Click(object sender, RoutedEventArgs e) =>
        RefreshManagedModels();

    private void RefreshManagedModels()
    {
        if (_managedModelInventory is null)
        {
            return;
        }
        try
        {
            _managedModelLibrary = _managedModelInventory.Synchronize(_storageSettings);
            _managedModelsInitialized = true;
            ApplyManagedModelFilter();
            ManagedModelOperationStatusText.Text = LF("Models.Library.Count", _managedModelLibrary.Count);
        }
        catch
        {
            ManagedModelOperationStatusText.Text = L("Models.Library.RefreshFailed");
        }
    }

    private void RefreshManagedModelLocalization()
    {
        ManagedModelsExpander.Header = L("Models.Library.Title");
        ManagedModelsHelpText.Text = L("Models.Library.Help");
        ManagedModelSearchBox.ToolTip = L("Models.Library.SearchHint");
        RefreshManagedModelsButton.Content = L("Models.Library.Refresh");
        CancelManagedModelOperationButton.Content = L("Common.Cancel");
        PopulateManagedModelFilter();
        if (_managedModelsInitialized)
        {
            ApplyManagedModelFilter();
        }
    }

    private void PopulateManagedModelFilter()
    {
        if (ManagedModelFilterComboBox is null)
        {
            return;
        }
        var selected = (ManagedModelFilterComboBox.SelectedItem as ManagedModelFilterOption)?.Id ?? "all";
        var options = new List<ManagedModelFilterOption>
        {
            new("all", L("Models.Filter.All")),
            new("installed", L("Models.Filter.Installed")),
            new("removed", L("Models.Filter.Removed")),
            new("attention", L("Models.Filter.Attention")),
            new("role:core", L("Models.Role.core")),
            new("role:vision", L("Models.Role.vision")),
            new("role:localizer", L("Models.Role.localizer")),
            new("role:speech", L("Models.Role.speech")),
            new("role:executor", L("Models.Role.executor")),
            new("role:reranker", L("Models.Role.reranker")),
            new("role:external", L("Models.Role.external"))
        };
        ManagedModelFilterComboBox.ItemsSource = options;
        ManagedModelFilterComboBox.DisplayMemberPath = nameof(ManagedModelFilterOption.Text);
        ManagedModelFilterComboBox.SelectedItem = options.FirstOrDefault(option => option.Id == selected) ?? options[0];
    }

    private void ManagedModelFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (_managedModelsInitialized)
        {
            ApplyManagedModelFilter();
        }
    }

    private void ApplyManagedModelFilter()
    {
        var query = ManagedModelSearchBox.Text.Trim();
        var filter = (ManagedModelFilterComboBox.SelectedItem as ManagedModelFilterOption)?.Id ?? "all";
        ManagedModelItemsControl.ItemsSource = _managedModelLibrary
            .Where(card => MatchesFilter(card, filter))
            .Where(card => string.IsNullOrWhiteSpace(query)
                || card.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || card.RepositoryId.Contains(query, StringComparison.OrdinalIgnoreCase)
                || card.Consumers.Any(consumer => consumer.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase)))
            .OrderBy(card => RoleOrder(card.Role))
            .ThenBy(card => card.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(CreateManagedModelViewModel)
            .ToList();
    }

    private ManagedModelCardViewModel CreateManagedModelViewModel(ManagedModelArtifactCard card) => new()
    {
        Card = card,
        RoleText = L($"Models.Role.{card.Role}"),
        StatusText = L($"Models.Status.{card.Status}"),
        SizeText = LF("Models.Library.Size", ComponentCardViewModel.FormatBytes(card.StoredBytes), ComponentCardViewModel.FormatBytes(card.TotalBytes)),
        SourceText = LF("Models.Library.Source", card.RepositoryId, ShortRevision(card.Revision)),
        ConsumersText = LF(
            "Models.Library.Consumers",
            card.Consumers.Count == 0 ? L("Common.Unknown") : string.Join(", ", card.Consumers.Select(LocalizeManagedModelConsumer))),
        PathText = LF("Models.Library.Path", card.InstallDirectory),
        WarningText = BuildManagedModelWarning(card),
        DownloadActionText = card.Status == ManagedModelStatuses.Installed
            ? L("Models.Action.Reinstall")
            : card.Status == ManagedModelStatuses.Paused
                ? L("Models.Action.Resume")
                : L("Models.Action.Download"),
        VerifyActionText = L("Models.Action.Verify"),
        RemoveActionText = L("Models.Action.RemoveFiles")
    };

    private async void ManagedModelDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_managedModelOperationActive
            || _managedModelAcquisition is null
            || sender is not WpfButton { Tag: ManagedModelCardViewModel viewModel }
            || !viewModel.CanAcquire)
        {
            return;
        }
        var card = viewModel.Card;
        var reinstall = viewModel.CanReinstall;
        var fileList = string.Join(Environment.NewLine, card.Files.Select(file => $"• {file.RelativePath} — {ComponentCardViewModel.FormatBytes(file.SizeBytes)}"));
        var confirmation = WpfMessageBox.Show(
            this,
            LF(
                reinstall ? "Models.Confirm.Reinstall" : "Models.Confirm.Download",
                card.DisplayName,
                fileList,
                ComponentCardViewModel.FormatBytes(card.TotalBytes),
                card.InstallDirectory,
                card.RepositoryId,
                ShortRevision(card.Revision),
                card.License),
            L(reinstall ? "Models.Title.Reinstall" : "Models.Title.Download"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }
        await RunManagedModelOperationAsync(
            viewModel,
            (progress, token) => reinstall
                ? _managedModelAcquisition.ReinstallAsync(card.ModelArtifactId, progress, token)
                : _managedModelAcquisition.DownloadAsync(card.ModelArtifactId, progress, token));
    }

    private async void ManagedModelVerifyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_managedModelOperationActive
            || _managedModelAcquisition is null
            || sender is not WpfButton { Tag: ManagedModelCardViewModel viewModel }
            || !viewModel.CanVerify)
        {
            return;
        }
        var card = viewModel.Card;
        var runtimeCheck = card.ModelArtifactId is ManagedModelCatalog.KimiMediumArtifactId
            or ManagedModelCatalog.FlorenceLargeArtifactId;
        if (runtimeCheck)
        {
            var confirmation = WpfMessageBox.Show(
                this,
                L("Models.Confirm.RuntimeVerify"),
                L("Models.Title.Verify"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            if (confirmation != MessageBoxResult.Yes)
            {
                return;
            }
        }
        await RunManagedModelOperationAsync(
            viewModel,
            async (progress, token) =>
            {
                var updated = await _managedModelAcquisition.VerifyAsync(card.ModelArtifactId, progress, token);
                if (runtimeCheck && updated.Status == ManagedModelStatuses.NeedsVerification)
                {
                    updated = await _managedModelRuntimeCompatibility!.VerifyAsync(card.ModelArtifactId, token);
                }
                return updated;
            });
    }

    private void ManagedModelRemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_managedModelOperationActive
            || _managedModelRemoval is null
            || sender is not WpfButton { Tag: ManagedModelCardViewModel viewModel }
            || !viewModel.CanRemove)
        {
            return;
        }
        var card = viewModel.Card;
        var warningKey = card.IsSystem ? "Models.Confirm.RemoveSystem" : "Models.Confirm.Remove";
        var confirmation = WpfMessageBox.Show(
            this,
            LF(
                warningKey,
                card.DisplayName,
                ComponentCardViewModel.FormatBytes(card.StoredBytes),
                string.Join(", ", card.Consumers.Select(LocalizeManagedModelConsumer))),
            L("Models.Title.Remove"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }
        try
        {
            var result = _managedModelRemoval.RemoveFiles(card.ModelArtifactId, includePartialFiles: true);
            ManagedModelOperationStatusText.Text = LF("Models.Result.Removed", ComponentCardViewModel.FormatBytes(result.RemovedBytes));
            RefreshManagedModels();
        }
        catch
        {
            ManagedModelOperationStatusText.Text = L("Models.Result.RemoveFailed");
        }
    }

    private async Task RunManagedModelOperationAsync(
        ManagedModelCardViewModel viewModel,
        Func<IProgress<ManagedModelDownloadProgress>, CancellationToken, Task<ManagedModelArtifactCard>> operation)
    {
        _managedModelOperationActive = true;
        ManagedModelSearchBox.IsEnabled = false;
        ManagedModelFilterComboBox.IsEnabled = false;
        RefreshManagedModelsButton.IsEnabled = false;
        ManagedModelItemsControl.IsEnabled = false;
        viewModel.IsBusy = true;
        _managedModelOperationCts?.Cancel();
        _managedModelOperationCts?.Dispose();
        _managedModelOperationCts = new CancellationTokenSource();
        CancelManagedModelOperationButton.Visibility = Visibility.Visible;
        var progress = new Progress<ManagedModelDownloadProgress>(value =>
        {
            viewModel.ProgressPercent = value.TotalBytes <= 0 ? 0 : Math.Clamp(value.DownloadedBytes * 100d / value.TotalBytes, 0, 100);
            viewModel.ProgressText = LF(
                "Models.Progress",
                value.FileName,
                ComponentCardViewModel.FormatBytes(value.DownloadedBytes),
                ComponentCardViewModel.FormatBytes(value.TotalBytes));
        });
        try
        {
            await operation(progress, _managedModelOperationCts.Token);
            ManagedModelOperationStatusText.Text = L("Models.Result.OperationComplete");
        }
        catch (OperationCanceledException)
        {
            ManagedModelOperationStatusText.Text = L("Models.Result.OperationCancelled");
        }
        catch
        {
            ManagedModelOperationStatusText.Text = L("Models.Result.OperationFailed");
        }
        finally
        {
            viewModel.IsBusy = false;
            ManagedModelSearchBox.IsEnabled = true;
            ManagedModelFilterComboBox.IsEnabled = true;
            RefreshManagedModelsButton.IsEnabled = true;
            ManagedModelItemsControl.IsEnabled = true;
            CancelManagedModelOperationButton.Visibility = Visibility.Collapsed;
            _managedModelOperationActive = false;
            _managedModelOperationCts.Dispose();
            _managedModelOperationCts = null;
            RefreshManagedModels();
        }
    }

    private void CancelManagedModelOperationButton_Click(object sender, RoutedEventArgs e) =>
        _managedModelOperationCts?.Cancel();

    private string BuildManagedModelWarning(ManagedModelArtifactCard card)
    {
        if (!card.IsManaged)
        {
            return L("Models.Warning.ExternalReadOnly");
        }
        if (!card.SupportsDirectDownload)
        {
            return L("Models.Warning.LegacyManager");
        }
        if (!string.IsNullOrWhiteSpace(card.LastError))
        {
            return L("Models.Warning.NeedsAttention");
        }
        return card.IsSystem ? L("Models.Warning.System") : string.Empty;
    }

    private string LocalizeManagedModelConsumer(ManagedModelConsumer consumer)
    {
        var key = $"Models.Consumer.{consumer.Id}";
        var localized = L(key);
        return string.Equals(localized, key, StringComparison.Ordinal)
            ? consumer.DisplayName
            : localized;
    }

    private static bool MatchesFilter(ManagedModelArtifactCard card, string filter) => filter switch
    {
        "installed" => card.Status == ManagedModelStatuses.Installed,
        "removed" => card.Status == ManagedModelStatuses.FilesRemoved,
        "attention" => card.Status is ManagedModelStatuses.Corrupted
            or ManagedModelStatuses.RuntimeIncompatible
            or ManagedModelStatuses.SourceUnavailable
            or ManagedModelStatuses.NeedsVerification,
        _ when filter.StartsWith("role:", StringComparison.Ordinal) =>
            string.Equals(card.Role, filter[5..], StringComparison.OrdinalIgnoreCase),
        _ => true
    };

    private static int RoleOrder(string role) => role switch
    {
        ManagedModelRoles.Core => 0,
        ManagedModelRoles.Vision => 1,
        ManagedModelRoles.Localizer => 2,
        ManagedModelRoles.Speech => 3,
        ManagedModelRoles.Executor => 4,
        ManagedModelRoles.Reranker => 5,
        ManagedModelRoles.Tool => 6,
        _ => 7
    };

    private static string ShortRevision(string revision) => string.IsNullOrWhiteSpace(revision)
        ? "—"
        : revision.Length <= 12 ? revision : revision[..12];

    private static string ParseRevision(string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            return string.Empty;
        }
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var resolveIndex = Array.FindIndex(parts, part => string.Equals(part, "resolve", StringComparison.OrdinalIgnoreCase));
        return resolveIndex >= 0 && resolveIndex + 1 < parts.Length
            ? parts[resolveIndex + 1]
            : string.Empty;
    }
}

public sealed record ManagedModelFilterOption(string Id, string Text);
