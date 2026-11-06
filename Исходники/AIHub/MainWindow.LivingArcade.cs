using System.Text.Json;
using System.Windows;
using AIHub.Controls;
using AIHub.Controls.LivingArcade;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.LivingArcade;

namespace AIHub;

public partial class MainWindow
{
    private LivingArcadeControl? _arcadePage;
    private LivingArcadeGeneration? _arcadeGeneration;
    private LivingArcadeRunStore? _arcadeStore;
    private LivingArcadeModelPaths? _arcadePaths;
    private bool _openingArcade;

    private async Task OpenLivingArcadeAsync()
    {
        if (_openingArcade) return;
        _openingArcade = true;
        try
        {
            _arcadeStore ??= new();
            if (_backgroundOperations is { } background)
            {
                LivingArcadePendingWork.DiscardDeleted(background, _arcadeStore);
                if (LivingArcadePendingWork.Find(background) is { } pending)
                {
                    RevealLivingArcade(pending.Paths).ShowPendingOperation(pending.State);
                    return;
                }
            }
            if (_backgroundOperations?.BlocksNewWork == true)
            { StatusText.Text = L("Arcade.Busy"); return; }
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            var prepare = new LivingArcadePreparationWindow(this, _storageSettings,
                _appSettings.ModelDownloads.MaximumParallelConnections, L, _backgroundLifetime.Token);
            if (prepare.ShowDialog() != true || prepare.ModelPaths is null) return;
            var page = RevealLivingArcade(prepare.ModelPaths);
            _ = page.WarmAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        { ComponentAuditFailures.Report(error); StatusText.Text = L("Arcade.Failed"); }
        finally { _openingArcade = false; }
    }

    private LivingArcadeControl RevealLivingArcade(LivingArcadeModelPaths paths)
    {
        if (_arcadePaths is not null && _arcadePaths != paths)
        { _arcadePage?.Dispose(); _arcadePage = null; }
        _arcadePaths = paths;
        _arcadeStore ??= new();
        _arcadeGeneration ??= new(_userContextService);
        if (_arcadePage is null)
        {
            _arcadePage = new(L, _arcadeStore, _arcadeGeneration, paths,
                () => _localizationService.CurrentLanguageCode, _backgroundLifetime.Token);
            _arcadePage.HomeRequested += () => ScenarioNavigationPage.ShowHome();
            _arcadePage.IsVisibleChanged += (_, _) => RefreshLivingArcadeHeader();
        }
        ShowBackgroundScenarioPage(WorkStartPage);
        foreach (var page in new FrameworkElement[] { FinancialPage, CapturePage, GenerationPage,
            ImageUtilityPage, MusicPage, VectorPage, RagBooksPageHost }) page.Visibility = Visibility.Collapsed;
        ScenarioNavigationPage.Visibility = Visibility.Visible;
        ScenarioNavigationPage.ShowLivingArcade(_arcadePage);
        RefreshLivingArcadeHeader();
        return _arcadePage;
    }

    private void RefreshLivingArcadeHeader()
    {
        var visible = _arcadePage?.IsVisible == true && ScenarioNavigationPage.IsLivingArcade;
        ArcadeHeaderTitle.Text = visible ? L("Arcade.Title") : "";
        ArcadeHeaderTitle.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RegisterLivingArcadeBackgroundOperations()
    {
        _backgroundOperations!.Register(LivingArcadeGeneration.WarmupKind, async (state, token) =>
        {
            var input = state.Input.Deserialize<LivingArcadeWarmupRequest>()
                ?? throw new InvalidOperationException("Arcade.Ai.InvalidCheckpoint");
            await RevealLivingArcade(input.Paths).WarmAsync(state, token);
        });
        _backgroundOperations.Register(LivingArcadeGeneration.FirstEventsKind, async (state, token) =>
        {
            var input = state.Input.Deserialize<LivingArcadeFirstEventsRequest>()
                ?? throw new InvalidOperationException("Arcade.Ai.InvalidCheckpoint");
            await RevealLivingArcade(input.Paths).RestoreFirstEventsAsync(input.RunId, state, token);
        });
        _backgroundOperations.Register(LivingArcadeGeneration.Kind, async (state, token) =>
        {
            var input = state.Input.Deserialize<LivingArcadeGenerationRequest>()
                ?? throw new InvalidOperationException("Arcade.Ai.InvalidCheckpoint");
            await RevealLivingArcade(input.Paths).RestoreGenerationAsync(input.RunId, state, token);
        });
        Closed += (_, _) => { _arcadePage?.Dispose(); _arcadeGeneration?.Dispose(); };
    }

    private void ViewLivingArcadeResult(BackgroundOperationNotice notice)
    {
        RestoreFromTray();
        if (_arcadePaths is not null) RevealLivingArcade(_arcadePaths);
        _backgroundOperations!.Acknowledge(notice.Id);
    }
}
