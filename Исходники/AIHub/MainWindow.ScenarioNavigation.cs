using System.Windows;
using AIHub.Services;

namespace AIHub;

public partial class MainWindow
{
    private void InitializeScenarioNavigation()
    {
        WorkStartPage.Children.Remove(PreviousWorkExpander);
        ScenarioNavigationPage.AttachSandboxHistory(PreviousWorkExpander);
        ScenarioNavigationPage.StateChanged += RefreshScenarioNavigationState;
        ScenarioNavigationPage.StateChanged += RefreshLivingArcadeHeader;
        ScenarioNavigationPage.IsVisibleChanged += (_, _) => RefreshWorkStartWorkspaceLayout();
        ScenarioNavigationPage.VideoPreparationCreated += ConfigureVideoConcept;
        ScenarioNavigationPage.SongClipCreated += ConfigureSongClip;
        ScenarioNavigationPage.PrepareShorts = PrepareShortsEntry;
        ScenarioNavigationPage.OpenVisualWorkspace = ConfigureVisualScenario;
        ScenarioNavigationPage.LetItOutChatCreated += ConfigureLetItOutChat;
        ScenarioNavigationPage.ScenarioRequested += async id =>
        {
            PrepareScenarioNavigation(id);
            switch (id)
            {
                case ScenarioNavigationCatalog.GameSounds:
                    await ScenarioNavigationPage.OpenGameSoundsAsync(this, () => _localizationService.CurrentLanguageCode);
                    break;
                case ScenarioNavigationCatalog.OneC:
                    ScenarioNavigationPage.ShowOneC(this);
                    break;
                case ScenarioNavigationCatalog.SpeechAssets:
                    ScenarioNavigationPage.ShowSpeechAssets(this);
                    break;
                case ScenarioNavigationCatalog.LivingArcade:
                    await OpenLivingArcadeAsync();
                    break;
                case ScenarioNavigationCatalog.LetItOut:
                    await OpenLetItOutAsync();
                    break;
                case ScenarioNavigationCatalog.Dictation:
                    DockDictation();
                    break;
                case ScenarioNavigationCatalog.BookConversion:
                    OpenBookConversionUtility();
                    break;
                case ScenarioNavigationCatalog.OfficeReader:
                    OpenOfficeReader();
                    break;
                case ScenarioNavigationCatalog.OfficeEditor:
                    OpenOfficeEditor();
                    break;
                case ScenarioNavigationCatalog.LyricVideo:
                    OpenLyricVideoUtility();
                    break;
                case ScenarioNavigationCatalog.VideoEditor:
                    OpenVideoEditorUtility();
                    break;
                case ScenarioNavigationCatalog.VideoUpscale:
                    OpenUpscaleUtility();
                    break;
                case ScenarioNavigationCatalog.Metadata:
                    OpenMetadataUtility();
                    break;
                case ScenarioNavigationCatalog.RagBooks:
                    OpenRagBooksScenario();
                    break;
                case ScenarioNavigationCatalog.CharacterKnowledge:
                    ScenarioNavigationPage.ShowCharacterKnowledge(this, () => _localizationService.CurrentLanguageCode);
                    break;
                case ScenarioNavigationCatalog.Publisher:
                    OpenPublisherScenario();
                    break;
                case ScenarioNavigationCatalog.Music:
                    OpenMusicScenario();
                    break;
                case ScenarioNavigationCatalog.ImageUtility:
                    OpenImageUtilityScenario();
                    break;
                case ScenarioNavigationCatalog.VectorGeneration:
                    OpenVectorGenerationScenario();
                    break;
                case ScenarioNavigationCatalog.VectorEditor:
                    OpenVectorEditorScenario();
                    break;
                case ScenarioNavigationCatalog.ImageGeneration:
                    OpenImageGenerationScenario();
                    break;
                case ScenarioNavigationCatalog.Island:
                    OpenIslandEditor();
                    break;
                case ScenarioNavigationCatalog.MailHub:
                    OpenMailHub();
                    break;
                case ScenarioNavigationCatalog.Capture:
                    OpenCaptureScenario();
                    break;
                case ScenarioNavigationCatalog.Finance:
                    OpenFinancialScenario();
                    break;
                case ScenarioNavigationCatalog.Literary:
                    SelectLiteraryScenarioButton_Click(this, new RoutedEventArgs());
                    break;
                case ScenarioNavigationCatalog.Images:
                    SelectImageAnalysisButton_Click(this, new RoutedEventArgs());
                    break;
                case ScenarioNavigationCatalog.ImageLayers:
                    await OpenImageLayersAsync();
                    break;
                case ScenarioNavigationCatalog.Sandbox:
                    SelectReasoningModeButton_Click(this, new RoutedEventArgs());
                    break;
            }
        };
    }

    private void PrepareScenarioNavigation(string id)
    {
        CapturePage.Visibility = Visibility.Collapsed;
        GenerationPage.Visibility = Visibility.Collapsed;
        VectorPage.Visibility = Visibility.Collapsed;
        ImageUtilityPage.Visibility = Visibility.Collapsed;
        MusicPage.Visibility = Visibility.Collapsed;
        RagBooksPageHost.Visibility = Visibility.Collapsed;
        if (id != ScenarioNavigationCatalog.Finance)
        {
            FinancialPage.Visibility = Visibility.Collapsed;
            ScenarioNavigationPage.Visibility = Visibility.Visible;
        }
    }

    private void RefreshScenarioNavigationState()
    {
        RefreshWorkStartWorkspaceLayout();
        if (WorkStartPage.Visibility != Visibility.Visible) return;
        if (ScenarioNavigationPage.IsShorts && ScenarioNavigationPage.Shorts is { } shorts) ConfigureShorts(shorts);
        BackFromWorkStartButton.Visibility = ScenarioNavigationPage.IsVideoPreparation || ScenarioNavigationPage.IsSongClip || ScenarioNavigationPage.IsLetItOutChat || ScenarioNavigationPage.IsLivingArcade ? Visibility.Collapsed : Visibility.Visible;
        if (ScenarioNavigationPage.IsImageLayers || ScenarioNavigationPage.IsCharacterKnowledge) { StatusText.Text = ""; return; }
        if (ScenarioNavigationPage.IsVisualScenario || ScenarioNavigationPage.IsShorts || ScenarioNavigationPage.IsVideoPreparation || ScenarioNavigationPage.IsSongClip || ScenarioNavigationPage.IsLetItOutChat || ScenarioNavigationPage.IsLivingArcade) { StatusText.Text = ""; return; }
        if (ScenarioNavigationPage.PlaceholderScenarioId is not null) { StatusText.Text = L("Navigation.Soon"); return; }
        if (RagBooksPageHost.Visibility == Visibility.Visible) { StatusText.Text = ""; return; }
        if (VectorPage.Visibility == Visibility.Visible || MusicPage.Visibility == Visibility.Visible || FinancialPage.Visibility == Visibility.Visible || CapturePage.Visibility == Visibility.Visible || GenerationPage.Visibility == Visibility.Visible || ImageUtilityPage.Visibility == Visibility.Visible) { StatusText.Text = ""; return; }
        if (ScenarioNavigationPage.IsSandboxLanding) RefreshPreviousSessions();
        StatusText.Text = L(ScenarioNavigationPage.IsSandboxLanding
            ? "Navigation.SandboxStatus" : "Status.WorkStartOpened");
    }
}
