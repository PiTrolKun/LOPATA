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
            if (id != ScenarioNavigationCatalog.Finance)
            {
                FinancialPage.Visibility = Visibility.Collapsed;
                ScenarioNavigationPage.Visibility = Visibility.Visible;
            }
            switch (id)
            {
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
        if (FinancialPage.Visibility == Visibility.Visible) { StatusText.Text = ""; return; }
        if (ScenarioNavigationPage.IsSandboxLanding) RefreshPreviousSessions();
        StatusText.Text = L(ScenarioNavigationPage.IsSandboxLanding
            ? "Navigation.SandboxStatus" : "Status.WorkStartOpened");
    }
}
