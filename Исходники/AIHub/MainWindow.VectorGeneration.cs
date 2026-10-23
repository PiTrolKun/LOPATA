using System.Windows;
using AIHub.Services;
using AIHub.Services.VectorGraphics;

namespace AIHub;

public partial class MainWindow
{
    private void ConfigureVectorGeneration()
    {
        VectorPage.ConfigureOptions(_appSettings.VectorGraphics, value => { _appSettings.VectorGraphics = value; _appSettingsStore.Save(_appSettings); });
        VectorPage.Configure(L, _storageSettings, _userProfile.DisplayName);
    }
    private void OpenVectorGenerationScenario()
    {
        ConfigureVectorGeneration();
        VectorPage.OpenSelection();
        if (ScenarioNavigationPage.IsHome) ScenarioNavigationPage.SelectDirection(ScenarioNavigationCatalog.Creation);
        foreach (var page in new FrameworkElement[] { ScenarioNavigationPage, FinancialPage, CapturePage, GenerationPage, ImageUtilityPage, MusicPage, RagBooksPageHost }) page.Visibility = Visibility.Collapsed;
        VectorPage.Visibility = Visibility.Visible; StatusText.Text = "";
    }
    private void RegisterVectorBackgroundOperation()
    {
        VectorPage.WorkspaceChanged += RefreshGenerationHeader;
        VectorPage.IsVisibleChanged += (_, _) => RefreshGenerationHeader();
        _backgroundOperations!.Register(VectorStore.BackgroundKind, (state, token) => { ConfigureVectorGeneration(); return VectorPage.ResumeAsync(state, token); });
        _backgroundOperations.Register(VectorPreparation.BackgroundKind, (state, token) => { ConfigureVectorGeneration(); return VectorPage.ResumeAsync(state, token); });
        _backgroundOperations.Changed += () => Dispatcher.BeginInvoke(VectorPage.RefreshBackgroundStatus);
        Closed += (_, _) => VectorPage.Dispose();
    }
}
