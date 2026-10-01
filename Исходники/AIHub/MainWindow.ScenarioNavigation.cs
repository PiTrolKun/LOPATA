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
            switch (id)
            {
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
        if (ScenarioNavigationPage.IsSandboxLanding) RefreshPreviousSessions();
        StatusText.Text = L(ScenarioNavigationPage.IsSandboxLanding
            ? "Navigation.SandboxStatus" : "Status.WorkStartOpened");
    }
}
