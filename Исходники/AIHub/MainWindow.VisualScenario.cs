using System.Windows;
using AIHub.Controls.VisualScenario;
using AIHub.Services;
using AIHub.Services.VisualScenario;

namespace AIHub;

public partial class MainWindow
{
    private VisualScenarioControl? _visualScenario;

    private VisualScenarioControl? ConfigureVisualScenario(bool prepare)
    {
        try
        {
            var components = new VisualComponents(_storageSettings, _appSettings.ModelDownloads.MaximumParallelConnections);
            _visualScenario ??= new(L, metadataAuthor: () => _userProfile.DisplayName);
            var pair = prepare ? VisualPreparation.Enter(this, L, components, _visualScenario.Preferred) : null;
            if (prepare && pair is null) return null;
            _visualScenario.Configure(L, components, pair);
            if (prepare) _visualScenario.WarmAdvisorOnEntry();
            return _visualScenario;
        }
        catch (OperationCanceledException) when (prepare) { return null; }
        catch (Exception error) when (prepare)
        {
            ComponentAuditFailures.Report(error);
            System.Windows.MessageBox.Show(this, L(error.Message), L("Visual.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
    }

    private VisualScenarioControl RevealVisualScenario()
    {
        ShowBackgroundScenarioPage(WorkStartPage);
        foreach (var page in new FrameworkElement[] { FinancialPage, CapturePage, GenerationPage,
            ImageUtilityPage, MusicPage, VectorPage, RagBooksPageHost }) page.Visibility = Visibility.Collapsed;
        ScenarioNavigationPage.Visibility = Visibility.Visible;
        if (!ScenarioNavigationPage.IsVisualScenario)
            ScenarioNavigationPage.OpenScenario(ScenarioNavigationCatalog.VisualScenario, prepareShorts: false, prepareVisual: false);
        return ScenarioNavigationPage.VisualWorkspace!;
    }
}
