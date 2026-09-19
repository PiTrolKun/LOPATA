using System.Windows;

namespace AIHub;

public partial class MainWindow
{
    private void LiteraryCalibration_Click(object sender, RoutedEventArgs e) => LiteraryPage.OpenCalibration();
    private void LiteraryLayer_Click(object sender, RoutedEventArgs e)
    { if(sender is System.Windows.Controls.Button { Tag: string layer }) LiteraryPage.OpenStudioLayer(layer); }
    private void SelectLiteraryScenarioButton_Click(object sender, RoutedEventArgs e)
    {
        CancelCoreSpeech(revealFullText: false, "open_literary_navigation");
        if (!HideImageAnalysisPages()) return;
        if (!HideStandardPages()) return;
        LiteraryPage.Configure(L, language: _appSettings.LanguageCode, initialFolder: _storageSettings.Results.Locations.FirstOrDefault()?.Path ?? "");
        LiteraryPage.BackRequested -= LiteraryBackRequested;
        LiteraryPage.BackRequested += LiteraryBackRequested;
        LiteraryPage.HomeRequested -= LiteraryHomeRequested;
        LiteraryPage.HomeRequested += LiteraryHomeRequested;
        LiteraryPage.Visibility = Visibility.Visible;
        StatusText.Text = "";
    }

    private void LiteraryBackRequested() => ShowWorkStartPage();
    private void LiteraryHomeRequested() => BackToStartButton_Click(this, new RoutedEventArgs());

    private void RefreshLiteraryLocalization()
    {
        LiteraryScenarioPlaceholderButton.ToolTip = L("Literary.Calibration.Title");
        foreach(var button in new[]{LiteraryPromptsButton,LiteraryAnchorButton,LiteraryRagButton,LiteraryJellyButton,LiteraryWorkingButton})
        {
            var label = L(Equals(button.Tag,"Anchor") ? "Literary.Anchor.Title" : "Studio."+button.Tag);
            button.ToolTip=label; System.Windows.Automation.AutomationProperties.SetName(button,label);
        }
        System.Windows.Automation.AutomationProperties.SetName(
            LiteraryScenarioPlaceholderButton, L("Literary.Calibration.Title"));
        LiteraryScenarioTitleText.Text = L("Literary.Title");
        LiteraryScenarioDescriptionText.Text = L("Literary.Description");
        SelectLiteraryScenarioButton.Content = L("ImageAnalysis.Scenario.Select");
        LiteraryPage.Configure(L, LiteraryPage.ShowingProjects, _appSettings.LanguageCode, _storageSettings.Results.Locations.FirstOrDefault()?.Path ?? "");
    }
}
