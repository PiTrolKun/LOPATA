using System.Windows;

namespace AIHub;

public partial class MainWindow
{
    private void SelectLiteraryScenarioButton_Click(object sender, RoutedEventArgs e)
    {
        CancelCoreSpeech(revealFullText: false, "open_literary_navigation");
        HideImageAnalysisPages();
        HideStandardPages();
        LiteraryPage.Configure(L, language: _appSettings.LanguageCode, initialFolder: _storageSettings.Results.Locations.FirstOrDefault()?.Path ?? "");
        LiteraryPage.BackRequested -= LiteraryBackRequested;
        LiteraryPage.BackRequested += LiteraryBackRequested;
        LiteraryPage.HomeRequested -= LiteraryHomeRequested;
        LiteraryPage.HomeRequested += LiteraryHomeRequested;
        LiteraryPage.Visibility = Visibility.Visible;
        StatusText.Text = L("Literary.WorkHint");
    }

    private void LiteraryBackRequested() => ShowWorkStartPage();
    private void LiteraryHomeRequested() => BackToStartButton_Click(this, new RoutedEventArgs());

    private void RefreshLiteraryLocalization()
    {
        LiteraryScenarioTitleText.Text = L("Literary.Title");
        LiteraryScenarioDescriptionText.Text = L("Literary.Description");
        SelectLiteraryScenarioButton.Content = L("ImageAnalysis.Scenario.Select");
        LiteraryPage.Configure(L, LiteraryPage.ShowingProjects, _appSettings.LanguageCode, _storageSettings.Results.Locations.FirstOrDefault()?.Path ?? "");
    }
}
