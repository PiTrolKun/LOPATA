using System.Globalization;
using System.Windows;
using AIHub.Models;
using AIHub.Controls;
using AIHub.Services;

namespace AIHub;

public partial class MainWindow
{
    private readonly EngineSetsControl _engineSets = new();
    private CancellationTokenSource? _engineSetOperation;
    private bool _engineSetsInitialized;
    private bool _engineSetListRefreshing;

    private void InitializeEngineSetSettings()
    {
        _engineSets.Localize(L);
        RefreshEngineSetIndicator();
        _engineSets.RefreshRequested += RefreshEngineSetsAsync;
        _engineSets.ActionRequested += ChangeEngineSetAsync;
        _engineSets.CancelRequested += () => _engineSetOperation?.Cancel();
        _engineSets.IsVisibleChanged += async (_, _) =>
        {
            if (!_engineSetsInitialized || !_engineSets.IsVisible || _engineSetOperation is not null || _engineSetListRefreshing) return;
            _engineSetListRefreshing = true;
            _engineSets.Busy(true);
            try { await RefreshEngineSetsAsync(); }
            catch (OperationCanceledException) { }
            catch (Exception error) { EngineSetFailure(error); }
            finally { _engineSetListRefreshing = false; _engineSets.Busy(false); }
        };
        Closed += (_, _) => _engineSetOperation?.Cancel();
    }

    private async Task InitializeEngineSetsAsync()
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        OwnedProcessRegistry.Log("startup_check_started", "EngineSets");
        try
        {
            await EngineSetService.Shared.InitializeAsync(_backgroundLifetime.Token);
            _engineSetsInitialized = true;
            RefreshEngineSetIndicator();
            // The settings list is verified when opened. Startup only needs the
            // selected set, already fully verified by InitializeAsync.
            if (_engineSets.IsVisible) await RefreshEngineSetsAsync();
        }
        finally { OwnedProcessRegistry.Log("startup_check_finished", "EngineSets", detail: $"elapsedMs={timer.ElapsedMilliseconds}"); }
    }

    private async Task RefreshEngineSetsAsync()
    {
        if (!_engineSetsInitialized)
        {
            await EngineSetService.Shared.InitializeAsync(_backgroundLifetime.Token);
            _engineSetsInitialized = true;
        }
        var installed = await EngineSetService.Shared.GetInstalledAsync(_backgroundLifetime.Token);
        var state = EngineSetService.Shared.GetState();
        RefreshEngineSetIndicator();
        _engineSets.ShowSets(EngineSetService.Shared.GetCatalog().Select(set => new EngineSetDisplay(
            set.Id, DateTime.ParseExact(set.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("dd.MM.yyyy"),
            set.DescriptionKey, set.LimitKey, set.TechnicalDetails, set.Id == state.ActiveId,
            set.Id == EngineSetSelection.RecommendedId, installed.Contains(set.Id), set.DownloadBytes, set.InstalledBytes)).ToArray());
    }

    private void RefreshEngineSetIndicator()
    {
        if (!_engineSetsInitialized)
        {
            ActiveEngineSetText.Text = L("EngineSets.Status.Checking");
            ActiveEngineSetText.ToolTip = L("EngineSets.Status.CheckingDescription");
            return;
        }
        var state = EngineSetService.Shared.GetState();
        var set = EngineSetService.Shared.GetCatalog().Single(entry => entry.Id == state.ActiveId);
        var date = DateTime.ParseExact(set.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("dd.MM.yyyy");
        ActiveEngineSetText.Text = string.Format(L("EngineSets.Status.Active"), set.Id);
        ActiveEngineSetText.ToolTip = string.Format(L("EngineSets.Status.Description"), set.Id, date);
        System.Windows.Automation.AutomationProperties.SetName(ActiveEngineSetText, ActiveEngineSetText.Text);
    }

    private bool EngineSetChangeBlocked() => _backgroundOperations?.BlocksNewWork == true
        || (_backgroundOperations?.PausedOperations.Count ?? 0) > 0
        || _coreModelDownloadCts is { IsCancellationRequested: false }
        || _componentOperationCts is { IsCancellationRequested: false }
        || _managedModelOperationCts is { IsCancellationRequested: false }
        || _imageAnalysisBundleOperationCts is { IsCancellationRequested: false }
        || _imageAnalysisRuntimePreparationCts is { IsCancellationRequested: false }
        || _choiceScenarioRequestInProgress || _executorCts is { IsCancellationRequested: false }
        || _musicPoetryWindow?.IsWorking == true || _musicPoetryWindow?.IsModelDownloadActive == true;

    private async Task ChangeEngineSetAsync(string id, bool download)
    {
        if (EngineSetChangeBlocked()) { _engineSets.Status("EngineSets.Busy"); return; }
        using var auxiliary = _backgroundOperations?.BeginAuxiliary();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_backgroundLifetime.Token);
        _engineSetOperation = cancellation;
        _engineSets.Status("EngineSets.Working");
        try
        {
            await ApplicationBackgroundOperations.RetireModelsAsync();
            cancellation.Token.ThrowIfCancellationRequested();
            var licenses = EngineSetService.Shared.GetCatalog().Single(set => set.Id == id).LicenseIds;
            await ComponentLicenseGate.EnsureAsync(licenses, cancellation.Token);
            if (download)
            {
                var progress = new Progress<ManagedModelDownloadProgress>(value =>
                {
                    if (cancellation.IsCancellationRequested || _engineSetOperation != cancellation) return;
                    var percent = value.TotalBytes == 0 ? 0 : Math.Clamp(value.DownloadedBytes * 100d / value.TotalBytes, 0, 100);
                    _engineSets.Progress($"{L("EngineSets.Working")} {percent:F0}%");
                });
                EngineSetService.Shared.MaximumParallelConnections = _appSettings.ModelDownloads.MaximumParallelConnections;
                await EngineSetService.Shared.DownloadAsync(id, progress, cancellation.Token);
                _engineSets.Status("EngineSets.Downloaded");
            }
            else
            {
                await EngineSetService.Shared.ActivateAsync(id, cancellation.Token);
                RefreshEngineSetIndicator();
                _engineSets.Status("EngineSets.Changed");
            }
            await RefreshEngineSetsAsync();
        }
        finally { _engineSetOperation = null; }
    }

    private async Task OfferEngineSetAsync()
    {
        if (!_engineSetsInitialized || EngineSetChangeBlocked()) return;
        var offer = await EngineSetService.Shared.GetOfferAsync(_backgroundLifetime.Token);
        if (offer is null) return;
        var active = EngineSetService.Shared.GetState().ActiveId;
        string NameWithDate(string id)
        {
            var set = EngineSetService.Shared.GetCatalog().Single(entry => entry.Id == id);
            return id + " — " + DateTime.ParseExact(set.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("dd.MM.yyyy");
        }
        var answer = new EngineSetOfferWindow(this, NameWithDate(offer.Id), NameWithDate(active), L).ShowDialog();
        if (answer == true) await _engineSets.RunActionAsync(offer.Id, false);
        else EngineSetService.Shared.Decline(offer.Id);
    }

    private void EngineSetFailure(Exception error)
    {
        OwnedProcessRegistry.Log("engine_set_failed", "EngineSets", detail: error.GetType().Name);
        _engineSets.Status("EngineSets.Failed");
    }
}
