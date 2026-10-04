using System.Windows;
using AIHub.Services;
using AIHub.Controls;

namespace AIHub;

public partial class MainWindow
{
    private readonly ImageGenerationSettingsControl _generationSettings = new();
    private ImagePromptAssistant? _imagePromptAssistant;
    private void ConfigureImageGeneration()
    {
        ConfigureGenerationReferences();
        _imagePromptAssistant ??= new(_userContextService);
        GenerationPage.ConfigurePromptAssistant(_imagePromptAssistant, () =>
        {
            var check = _coreModelManager.Check(_storageSettings);
            return check.Availability == AIHub.Models.CoreModelAvailability.Installed ? check.ModelPath : null;
        });
        GenerationPage.ConfigureOutput(_appSettings.ImageGeneration, () => { _appSettingsStore.Save(_appSettings); RefreshGenerationSettings(); });
        GenerationPage.ConfigureMetadata(() => _userProfile.DisplayName);
        GenerationPage.Configure(L, _storageSettings, _appSettings.ModelDownloads?.MaximumParallelConnections ?? 0, _localizationService.CurrentLanguageCode);
    }
    private void RefreshGenerationSettings()
    { _generationSettings.Configure(_appSettings.ImageGeneration, L); RefreshGenerationHeader(); }
    private void RefreshGenerationHeader()
    {
        var visible = GenerationPage.IsVisible && GenerationPage.IsChat;
        GenerationModelButton.Visibility = GenerationClearButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        GenerationModelButton.IsEnabled = GenerationClearButton.IsEnabled = GenerationPage.CanChangeModel;
        GenerationModelButton.ToolTip = L("Generation.ChangeModel"); GenerationClearButton.ToolTip = L("Generation.ClearWorkspace");
        GenerationModelButton.Content = L("Generation.ModelSymbol");
        System.Windows.Automation.AutomationProperties.SetName(GenerationModelButton, L("Generation.ChangeModel"));
        System.Windows.Automation.AutomationProperties.SetName(GenerationClearButton, L("Generation.ClearWorkspace"));
        GenerationModelButton.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextPrimaryBrush");
    }
    private void GenerationModelButton_Click(object sender, RoutedEventArgs e) => GenerationPage.ShowModelMenu(GenerationModelButton);
    private void GenerationClearButton_Click(object sender, RoutedEventArgs e) => GenerationPage.ClearWorkspace();
    private void OpenImageGenerationScenario()
    {
        ImageUtilityPage.Visibility = Visibility.Collapsed;
        ConfigureImageGeneration();
        if (ScenarioNavigationPage.IsHome) ScenarioNavigationPage.SelectDirection(ScenarioNavigationCatalog.Creation);
        ScenarioNavigationPage.Visibility = Visibility.Collapsed;
        FinancialPage.Visibility = Visibility.Collapsed; CapturePage.Visibility = Visibility.Collapsed;
        GenerationPage.Visibility = Visibility.Visible; StatusText.Text = "";
    }
    private void RegisterImageGenerationBackgroundOperation()
    {
        GenerationPage.WorkspaceChanged += RefreshGenerationHeader;
        GenerationPage.IsVisibleChanged += (_, _) => RefreshGenerationHeader();
        ConfigureImageGeneration(); RefreshGenerationSettings();
        _backgroundOperations!.Register(ImageGenerationCatalog.BackgroundKind, async (state, token) =>
        { ConfigureImageGeneration(); await GenerationPage.ResumeAsync(state, token); });
        _backgroundOperations.Register(ImagePromptAssistant.BackgroundKind, async (state, token) =>
        { ConfigureImageGeneration(); await GenerationPage.ResumePromptAsync(state, token); });
        _backgroundOperations.Register(ImageReferenceAnalyzer.BackgroundKind, async (state, token) =>
        { ConfigureImageGeneration(); await GenerationPage.ResumeReferenceAsync(state, token); });
        _backgroundOperations.Changed += () => Dispatcher.BeginInvoke(GenerationPage.RefreshBackgroundStatus);
        Closed += (_, _) => { GenerationPage.DisposeRuntime(); _imagePromptAssistant?.Dispose(); _imageReferenceAnalyzer?.Dispose(); };
    }
}
