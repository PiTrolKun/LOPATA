using System.Windows;
using AIHub.Services;

namespace AIHub;

public partial class MainWindow
{
    private void ConfigureImageUtility() => ImageUtilityPage.Configure(L, _storageSettings,
        _appSettings.ModelDownloads?.MaximumParallelConnections ?? 0, _appSettings.ImageGeneration.Folder);

    private void OpenImageUtilityScenario()
    {
        ConfigureImageUtility();
        if (ScenarioNavigationPage.IsHome) ScenarioNavigationPage.SelectDirection(ScenarioNavigationCatalog.Utilities);
        ScenarioNavigationPage.Visibility = FinancialPage.Visibility = CapturePage.Visibility = GenerationPage.Visibility = Visibility.Collapsed;
        ImageUtilityPage.Visibility = Visibility.Visible;
        StatusText.Text = "";
    }

    private void RegisterImageUtilityBackgroundOperation()
    {
        ImageUtilityPage.ViewFileRequested += path =>
            _fileViewerService.Open(this, path, _appSettings.FileViewer, _isDarkTheme, _localizationService);
        ImageUtilityPage.IsVisibleChanged += (_, _) =>
        {
            if (ImageUtilityPage.IsVisible)
            {
                WorkStartPage.MaxWidth = double.PositiveInfinity;
                WorkStartPage.Margin = new Thickness(18, 16, 18, 16);
            }
            else
            {
                WorkStartPage.SetResourceReference(MaxWidthProperty, "UiMainContentMaxWidth");
                WorkStartPage.SetResourceReference(MarginProperty, "UiPageMargin");
            }
        };
        _backgroundOperations!.Register(ImageUtilityQueue.BackgroundKind, async (state, token) =>
        { ConfigureImageUtility(); await ImageUtilityPage.ResumeAsync(state, token); });
        _backgroundOperations.Changed += () =>
        { if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(ImageUtilityPage.RefreshBackgroundStatus); };
        Closed += (_, _) => ImageUtilityPage.DisposeRuntime();
    }
}
