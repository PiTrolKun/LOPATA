using System.Windows;
using AIHub.Controls;
using AIHub.Services;

namespace AIHub;

public partial class MainWindow
{
    private void ConfigureShorts(ShortsControl control)
    {
        control.Configure(L, ShortsModelsRoot, _appSettings.ModelDownloads?.MaximumParallelConnections ?? 0);
    }

    private string ShortsModelsRoot => _storageSettings.Models.Locations.Select(location => location.Path)
        .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path)) ?? "";

    private ShortsControl OpenShorts()
    {
        ShowBackgroundScenarioPage(WorkStartPage);
        foreach (var page in new FrameworkElement[] { FinancialPage, CapturePage, GenerationPage,
            ImageUtilityPage, MusicPage, VectorPage, RagBooksPageHost }) page.Visibility = Visibility.Collapsed;
        ScenarioNavigationPage.Visibility = Visibility.Visible;
        if (!ScenarioNavigationPage.IsShorts) ScenarioNavigationPage.OpenScenario(ScenarioNavigationCatalog.Shorts, prepareShorts: false);
        var control = ScenarioNavigationPage.Shorts!;
        ConfigureShorts(control);
        return control;
    }
}
