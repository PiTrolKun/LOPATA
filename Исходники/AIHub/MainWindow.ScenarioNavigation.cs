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
        ScenarioNavigationPage.ScenarioRequested += id =>
        {
            CapturePage.Visibility = Visibility.Collapsed;
            GenerationPage.Visibility = Visibility.Collapsed;
            ImageUtilityPage.Visibility = Visibility.Collapsed;
            MusicPage.Visibility = Visibility.Collapsed;
            if (id != ScenarioNavigationCatalog.Finance)
            {
                FinancialPage.Visibility = Visibility.Collapsed;
                ScenarioNavigationPage.Visibility = Visibility.Visible;
            }
            switch (id)
            {
                case ScenarioNavigationCatalog.Publisher:
                    OpenPublisherScenario();
                    break;
                case ScenarioNavigationCatalog.Music:
                    OpenMusicScenario();
                    break;
                case ScenarioNavigationCatalog.ImageUtility:
                    OpenImageUtilityScenario();
                    break;
                case ScenarioNavigationCatalog.ImageGeneration:
                    OpenImageGenerationScenario();
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
                case ScenarioNavigationCatalog.Sandbox:
                    SelectReasoningModeButton_Click(this, new RoutedEventArgs());
                    break;
            }
        };
    }

    private void RefreshScenarioNavigationState()
    {
        if (WorkStartPage.Visibility != Visibility.Visible) return;
        if (MusicPage.Visibility == Visibility.Visible || FinancialPage.Visibility == Visibility.Visible || CapturePage.Visibility == Visibility.Visible || GenerationPage.Visibility == Visibility.Visible || ImageUtilityPage.Visibility == Visibility.Visible) { StatusText.Text = ""; return; }
        if (ScenarioNavigationPage.IsSandboxLanding) RefreshPreviousSessions();
        StatusText.Text = L(ScenarioNavigationPage.IsSandboxLanding
            ? "Navigation.SandboxStatus" : "Status.WorkStartOpened");
    }
}
