using System.IO;
using System.Windows;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;

namespace AIHub;

public partial class MainWindow
{
    private RagBookWorkspaceControl? _ragBooksPage;
    private Task? _ragBooksResume;
    private string? _ragBooksAttention;

    private RagBookWorkspaceControl GetRagBooksPage()
    {
        if (_ragBooksPage is not null) return _ragBooksPage;
        var modelsRoot = _storageSettings.Models.Locations.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l.Path))?.Path
            ?? AppDataPaths.ComponentModelsDirectory;
        var resultsRoot = _storageSettings.Results.Locations.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l.Path))?.Path
            ?? Path.Combine(AppDataPaths.BaseDirectory, "Results");
        _ragBooksPage = new(L, modelsRoot, resultsRoot,
            _appSettings.ModelDownloads?.MaximumParallelConnections ?? 0,
            path => _fileViewerService.Open(this, path, _appSettings.FileViewer, _isDarkTheme, _localizationService),
            () => { if (_componentLicenses is not null) ShowComponentLicenses(
                _componentLicenses.Entries.Where(e => RagBookModels.LicenseIds.Contains(e.Id)).ToArray(), false); });
        _ragBooksPage.BackRequested += () => TryGoBackRagBooks();
        RagBooksPageHost.Content = _ragBooksPage;
        return _ragBooksPage;
    }

    private void OpenRagBooksScenario(bool showComponents = false)
    {
        var page = GetRagBooksPage();
        page.ConfigureConnections(_appSettings.ModelDownloads?.MaximumParallelConnections ?? 0);
        page.RefreshLocalization();
        if (ScenarioNavigationPage.IsHome) ScenarioNavigationPage.SelectDirection(ScenarioNavigationCatalog.Utilities);
        ScenarioNavigationPage.Visibility = FinancialPage.Visibility = CapturePage.Visibility =
            GenerationPage.Visibility = ImageUtilityPage.Visibility = MusicPage.Visibility = Visibility.Collapsed;
        RagBooksPageHost.Visibility = Visibility.Visible;
        if (showComponents) page.OpenComponents();
        StatusText.Text = "";
    }

    private bool TryGoBackRagBooks()
    {
        if (RagBooksPageHost.Visibility != Visibility.Visible) return false;
        if (_ragBooksPage?.GoBack() == true) return true;
        if (_ragBooksPage?.SaveForExit() == false) return true;
        RagBooksPageHost.Visibility = Visibility.Collapsed;
        ScenarioNavigationPage.Visibility = Visibility.Visible;
        ScenarioNavigationPage.ReturnFromScenario(); RefreshScenarioNavigationState(); return true;
    }

    private void RegisterRagBookBackgroundOperation()
    {
        _backgroundOperations!.Register(RagBookPreparation.Kind, (state, token) => GetRagBooksPage().ResumePreparationAsync(state, token));
        _backgroundOperations!.Register(RagBookAcquisitionWork.Kind,
            (state, token) =>
            {
                var page = GetRagBooksPage(); page.ConfigureConnections(_appSettings.ModelDownloads?.MaximumParallelConnections ?? 0);
                return page.ResumeAcquisitionAsync(state, token);
            });
        foreach (var sibling in new FrameworkElement[] { ScenarioNavigationPage, MusicPage, FinancialPage, CapturePage, GenerationPage, ImageUtilityPage })
            sibling.IsVisibleChanged += (_, _) => { if (sibling.IsVisible) RagBooksPageHost.Visibility = Visibility.Collapsed; };
        Closed += (_, _) => _ragBooksPage?.Dispose();
    }

    private void RevealRagBooksPage(bool components = false)
    {
        RestoreFromTray(); ShowBackgroundScenarioPage(WorkStartPage); OpenRagBooksScenario(components);
    }

    private void QueueRagBooksResume()
    {
        if (_ragBooksResume is { IsCompleted: false }) return;
        _ragBooksResume = ResumeAsync();
        async Task ResumeAsync()
        {
            try { await _backgroundOperations!.ResumeAsync(_backgroundLifetime.Token); }
            catch (OperationCanceledException) when (_backgroundLifetime.IsCancellationRequested) { }
            catch (Exception error) { ReportBackgroundFailure(error); }
        }
    }

    private void CheckpointRagBooks()
    {
        if (_ragBooksPage?.SaveForExit() == false) throw new IOException("Book workspace checkpoint was not confirmed.");
    }

    private void NotifyRagBooksWaiting()
    {
        var state = _backgroundOperations?.State;
        if (state?.Kind is not (RagBookAcquisitionWork.Kind or RagBookPreparation.Kind) || !state.RequiresDecision)
        { _ragBooksAttention = null; return; }
        var signature = state.Id + ":" + state.Detail;
        if (IsVisible || _ragBooksAttention == signature) return;
        if (_applicationTray?.Notify(L("RagBooks.AcquisitionTitle"), L("RagBooks.State.Waiting"), warning: true,
            onOpen: () => Dispatcher.BeginInvoke(() => RevealRagBooksPage(state.Kind == RagBookAcquisitionWork.Kind))) == true) _ragBooksAttention = signature;
    }
}
