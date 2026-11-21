using AIHub.Controls.ImageLayers;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.ImageLayers;
using System.Windows;

namespace AIHub;

public partial class MainWindow
{
    private ImageLayersControl? _imageLayers;
    internal bool ImageLayersExitPending => _processShutdownPending || _processShutdownComplete || _sessionEnding;

    private async Task OpenImageLayersAsync()
    {
        await EnsureApplicationStartupAsync();
        if (WorkStartPage.Visibility != Visibility.Visible || ScenarioNavigationPage.SelectedDirectionId != ScenarioNavigationCatalog.Analysis) return;
        var page = ShowImageLayersPage();
        if (_backgroundOperations?.State is not { } background) return;
        var notice = background.Notices.LastOrDefault(item => item.Kind == LayerAnalysisWork.Kind);
        var state = notice is not null ? _backgroundOperations.LoadResult(notice.Id) : background;
        if (state is { Kind: LayerAnalysisWork.Kind, Phase: BackgroundOperationPhase.Completed }
            && LayerAnalysisWork.Default.HasPreview(state.Id) && page.CanReceiveResult(LayerAnalysisWork.ReadInput(state)))
            await page.ViewResultAsync(state);
    }

    private ImageLayersControl ShowImageLayersPage()
    {
        if (_imageLayers is null)
        {
            var library = new ManagedModelLibraryStore();
            _imageLayers = new ImageLayersControl(this, L,
                () => _localizationService.CurrentLanguageCode.StartsWith("ru", StringComparison.OrdinalIgnoreCase),
                new ImageLayerAnalyzer(library), () => ImageLayerPreparation(library));
        }
        ScenarioNavigationPage.ShowImageLayers(_imageLayers);
        return _imageLayers;
    }

    private LayerAnalysisPreparation ImageLayerPreparation(ManagedModelLibraryStore library) =>
        new(library, _storageSettings, _appSettings.ModelDownloads?.MaximumParallelConnections ?? 0);

    private async Task ContinueSavedImageLayerAnalysisAsync(string id)
    {
        try { await _backgroundOperations!.ResumeAsync(id, _backgroundLifetime.Token); }
        catch (OperationCanceledException) when (_backgroundLifetime.IsCancellationRequested) { }
        catch (Exception error) { ReportBackgroundFailure(error); }
    }

    private async Task ResumeImageLayerAnalysisAsync(BackgroundOperationState state, CancellationToken token)
    {
        using var audit = ComponentAudit.CaptureOperation();
        var work = LayerAnalysisWork.Default;
        var input = LayerAnalysisWork.ReadInput(state);
        if (input.Extra?.Method == LayerAdditionalMethod.RetiredQwen && !work.HasPreview(state.Id))
            throw new BackgroundOperationFailedException("ImageLayers.AnalysisRetired");
        var snapshot = await Task.Run(() => work.OpenSource(state), token);
        var library = new ManagedModelLibraryStore();
        if (!work.HasPreview(state.Id) && !input.CompleteFromCurrentLayers(snapshot))
        {
            if (!IsVisible && _componentLicenses is { } licenses)
            {
                var required = LayerAnalysisPreparation.Licenses(input.Refine, input.MaskMethod, input.PortraitParts,
                    input.Extra, input.RecognizeText);
                var accepted = licenses.ReadReceipts();
                if (licenses.Entries.Any(entry => required.Contains(entry.Id) && !accepted.Any(receipt =>
                    receipt.Id == entry.Id && receipt.Terms == entry.Terms)))
                    throw new BackgroundOperationWaitingException("ImageLayers.ResumeComponents");
            }
            var prepare = ImageLayerPreparation(library);
            if (!await Task.Run(() => prepare.PrepareAsync(input.Refine, false, null, token,
                input.MaskMethod, input.PortraitParts, input.Extra, input.RecognizeText), token))
                throw new BackgroundOperationWaitingException("ImageLayers.ResumeComponents");
        }
        await work.RunAsync(state, snapshot, new ImageLayerAnalyzer(library), new Progress<LayerAnalysisProgress>(), token, library);
        if (_imageLayers is { IsVisible: true } page && page.CanReceiveResult(input))
            await page.ViewResultAsync(_backgroundOperations?.LoadResult(state.Id) ?? state);
    }

    private async Task ViewImageLayerResultAsync(BackgroundOperationNotice notice)
    {
        if (_backgroundOperations?.LoadResult(notice.Id) is not { } state) return;
        if (!LayerAnalysisWork.Default.HasPreview(state.Id)) { _backgroundOperations.Acknowledge(state.Id); return; }
        RestoreFromTray();
        if (WorkStartPage.Visibility != Visibility.Visible) ShowWorkStartPage();
        if (WorkStartPage.Visibility != Visibility.Visible) return;
        if (!ScenarioNavigationPage.IsImageLayers)
        {
            ScenarioNavigationPage.SelectDirection(ScenarioNavigationCatalog.Analysis);
            if (ScenarioNavigationPage.SelectedDirectionId != ScenarioNavigationCatalog.Analysis) return;
        }
        PrepareScenarioNavigation(ScenarioNavigationCatalog.ImageLayers);
        var page = ShowImageLayersPage();
        await page.ViewResultAsync(state);
    }
}
