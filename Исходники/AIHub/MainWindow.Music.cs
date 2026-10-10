using System.Windows;
using AIHub.Services;
using AIHub.Controls;

namespace AIHub;

public partial class MainWindow
{
    private MusicPoetryWindow? _musicPoetryWindow;
    private void OpenMusicPoetry(MusicWorkspaceControl workspace)
    {
        if (_musicPoetryWindow is not null)
        {
            if (_musicPoetryWindow.WindowState == WindowState.Minimized) _musicPoetryWindow.WindowState = WindowState.Normal;
            _musicPoetryWindow.Show(); _musicPoetryWindow.Activate(); return;
        }
        var window = new MusicPoetryWindow(() => workspace.Projects.Capture(), _storageSettings,
            _userContextService, L, _localizationService.CurrentLanguageCode);
        _musicPoetryWindow = window;
        void RefreshPoetry() => workspace.Generation.SetPoetryState(true, window.IsWorking);
        window.WorkingChanged += RefreshPoetry;
        window.Closed += (_, _) => {
            window.WorkingChanged -= RefreshPoetry;
            _musicPoetryWindow = null; workspace.Generation.SetPoetryState(false, false);
        };
        window.Show();
        RefreshPoetry();
    }
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
        MusicPage.CanRemoveModel = CanRemoveMusicModel;
        MusicPage.RemoveModelRequested += RemoveMusicModelAsync;
        MusicPage.PoetryRequested += OpenMusicPoetry;
        foreach (var page in new FrameworkElement[] { ScenarioNavigationPage, FinancialPage, CapturePage, GenerationPage, ImageUtilityPage })
            page.IsVisibleChanged += (_, _) => { if (page.IsVisible) MusicPage.Visibility = Visibility.Collapsed; };
        Closed += (_, _) => { _musicPoetryWindow?.Close(); MusicPage.Dispose(); };
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
