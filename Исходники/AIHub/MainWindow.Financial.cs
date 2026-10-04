using System.Windows;
using AIHub.Models;
using AIHub.Services;

namespace AIHub;

public partial class MainWindow
{
    private void ConfigureFinancialScenario() => FinancialPage.Configure(L, _appSettings.LanguageCode, _storageSettings, _userProfile, _userContextService);
    private void OpenFinancialScenario()
    {
        ImageUtilityPage.Visibility = Visibility.Collapsed;
        CancelCoreSpeech(revealFullText: false, "open_financial_scenario");
        ConfigureFinancialScenario();
        if (ScenarioNavigationPage.IsHome) ScenarioNavigationPage.SelectDirection(ScenarioNavigationCatalog.Experiments);
        ScenarioNavigationPage.Visibility = Visibility.Collapsed;
        FinancialPage.Visibility = Visibility.Visible;
        StatusText.Text = "";
    }
    private void RegisterFinancialBackgroundOperation()
    {
        _backgroundOperations!.Register(FinancialAnalysisPlan.BackgroundKind, async (state, token) =>
        {
            ConfigureFinancialScenario(); await FinancialPage.ResumeBackgroundAsync(state, token);
        });
        _backgroundOperations.Changed += () => Dispatcher.BeginInvoke(() => FinancialPage.RefreshBackgroundStatus());
        Closed += (_, _) => FinancialPage.DisposeRuntime();
    }
    private bool ViewFinancialBackgroundResult(BackgroundOperationNotice notice)
    {
        if (notice.Project is null) return false;
        ApplicationBackgroundOperations.PreserveHiddenWork = true;
        try
        {
            RestoreFromTray();
            if (!HideStandardPages() || !HideImageAnalysisPages()) return false;
            LiteraryPage.Visibility = Visibility.Collapsed;
            WorkStartPage.Visibility = Visibility.Visible;
            OpenFinancialScenario(); FinancialPage.Restore(notice.Project);
            return true;
        }
        finally { ApplicationBackgroundOperations.PreserveHiddenWork = false; }
    }
}
