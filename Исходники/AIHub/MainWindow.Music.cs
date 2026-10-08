using System.Windows;
using AIHub.Services;

namespace AIHub;

public partial class MainWindow
{
    private void ConfigureMusic()
    {
        MusicPage.ConfigureOutput(_appSettings.MusicOutputFolder, folder =>
        {
            var previous = _appSettings.MusicOutputFolder; _appSettings.MusicOutputFolder = folder;
            try { _appSettingsStore.Save(_appSettings); }
            catch { _appSettings.MusicOutputFolder = previous; throw; }
        });
        MusicPage.Configure(L, _storageSettings, _appSettings.ModelDownloads?.MaximumParallelConnections ?? 0);
    }

    private void InitializeMusicPreparation()
    {
        MusicPage.IsVisibleChanged += (_, _) => { if (MusicPage.IsVisible) ConfigureMusic(); RefreshGenerationHeader(); };
        MusicPage.WorkspaceChanged += RefreshGenerationHeader;
        foreach (var page in new FrameworkElement[] { ScenarioNavigationPage, FinancialPage, CapturePage, GenerationPage, ImageUtilityPage })
            page.IsVisibleChanged += (_, _) => { if (page.IsVisible) MusicPage.Visibility = Visibility.Collapsed; };
        Closed += (_, _) => MusicPage.Dispose();
    }

    private void OpenMusicScenario(bool checkPreparation = true)
    {
        ConfigureMusic();
        if (ScenarioNavigationPage.IsHome) ScenarioNavigationPage.SelectDirection(ScenarioNavigationCatalog.Creation);
        ScenarioNavigationPage.Visibility = FinancialPage.Visibility = CapturePage.Visibility =
            GenerationPage.Visibility = ImageUtilityPage.Visibility = Visibility.Collapsed;
        MusicPage.Visibility = Visibility.Visible;
        StatusText.Text = "";
        if (checkPreparation) _ = MusicPage.OpenAsync();
    }
}
