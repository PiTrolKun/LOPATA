using System.Windows;
using AIHub.Services;
using AIHub.Controls;

namespace AIHub;

public partial class MainWindow
{
    private MusicPoetryWindow? _musicPoetryWindow;
    private MusicPoetryWindow GetMusicPoetry()
    {
        if (_musicPoetryWindow is not null) return _musicPoetryWindow;
        var window = new MusicPoetryWindow(MusicPage.CapturePoetrySeed, _storageSettings,
            _userContextService, L, _localizationService.CurrentLanguageCode,
            interfaceLanguage: () => _localizationService.CurrentLanguageCode);
        _musicPoetryWindow = window;
        void RefreshPoetry() => MusicPage.SetPoetryState(true, window.IsWorking);
        window.WorkingChanged += RefreshPoetry;
        window.Closed += (_, _) => {
            window.WorkingChanged -= RefreshPoetry;
            _musicPoetryWindow = null; MusicPage.LeavePoetry(window); MusicPage.SetPoetryState(false, false);
        };
        RefreshPoetry(); return window;
    }
    private void OpenMusicPoetry(MusicWorkspaceControl workspace)
    {
        var poetry = GetMusicPoetry(); MusicPage.LeavePoetry(poetry); poetry.PresentAsWindow();
    }
    private void OpenMusicPoetryDirect()
    {
        MusicPage.ShowPoetry(GetMusicPoetry());
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
        MusicPage.ConfigureRhyme(_storageSettings, () => new MusicRhymeModel(_userContextService));
        MusicPage.ConfigureCovers(_storageSettings, () => new MusicRhymeModel(_userContextService));
    }

    private void InitializeMusicPreparation()
    {
        MusicPage.IsVisibleChanged += (_, _) => { if (MusicPage.IsVisible) ConfigureMusic(); RefreshGenerationHeader(); };
        MusicPage.WorkspaceChanged += RefreshGenerationHeader;
        MusicPage.CanRemoveModel = CanRemoveMusicModel;
        MusicPage.RemoveModelRequested += RemoveMusicModelAsync;
        MusicPage.PoetryRequested += OpenMusicPoetry;
        MusicPage.DirectPoetryRequested += OpenMusicPoetryDirect;
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
