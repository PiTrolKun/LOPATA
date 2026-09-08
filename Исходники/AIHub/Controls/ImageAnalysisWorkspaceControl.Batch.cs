using System.Windows;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Controls;

public partial class ImageAnalysisWorkspaceControl
{
    private ContentControl? _batchOverlay;
    private StackPanel? _batchOutputOptions;
    private System.Windows.Controls.RadioButton? _batchSingleOption;
    private System.Windows.Controls.RadioButton? _batchFolderOption;
    private System.Windows.Controls.ProgressBar? _batchFooterProgress;
    public bool IsBatch { get; private set; }
    public bool IsBatchSettings => IsBatch && _currentStep == ImageAnalysisLiterarySteps.Settings;
    public event EventHandler? MultipleSubscenarioRequested;
    public bool BatchSingleDocument => _batchSingleOption?.IsChecked != false;
    public ImageAnalysisLiterarySettings BatchSettings => ReadSettings();

    public void InitializeBatchControls()
    {
        MultipleScenarioButton.Click += (_, _) => MultipleSubscenarioRequested?.Invoke(this, EventArgs.Empty);
        _batchOverlay = new ContentControl { Visibility = Visibility.Collapsed, HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch, VerticalContentAlignment = System.Windows.VerticalAlignment.Stretch };
        _batchOverlay.SetResourceReference(BackgroundProperty, "PanelBrush");
        ((Grid)CenterPanel.Child).Children.Add(_batchOverlay);
        _batchOutputOptions = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 12, 0, 8) };
        _batchSingleOption = new() { GroupName = "BatchOutput", IsChecked = true, Margin = new Thickness(0, 5, 0, 5) };
        _batchFolderOption = new() { GroupName = "BatchOutput", Margin = new Thickness(0, 5, 0, 5) };
        _batchSingleOption.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        _batchFolderOption.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        var outputChoices = new StackPanel();
        outputChoices.Children.Add(_batchSingleOption); outputChoices.Children.Add(_batchFolderOption);
        var outputFrame = new Border
        {
            Child = outputChoices, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 0, 12),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        };
        outputFrame.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        outputFrame.SetResourceReference(Border.BackgroundProperty, "SecondaryButtonBackgroundBrush");
        _batchOutputOptions.Children.Add(outputFrame);
        var hint = new TextBlock { TextWrapping = TextWrapping.Wrap };
        hint.Text = _localize("Batch.CustomHint"); _batchOutputOptions.Children.Add(hint);
        SettingsPanel.Children.Insert(0, _batchOutputOptions);
        var footer = (Grid)FooterStatusText.Parent;
        footer.Children.Remove(FooterStatusText);
        var statusArea = new StackPanel { VerticalAlignment = System.Windows.VerticalAlignment.Center };
        Grid.SetColumn(statusArea, 1); statusArea.Children.Add(FooterStatusText);
        _batchFooterProgress = new() { Height = 9, Minimum = 0, Maximum = 100, Visibility = Visibility.Collapsed, Margin = new Thickness(14, 5, 0, 0) };
        statusArea.Children.Add(_batchFooterProgress); footer.Children.Add(statusArea);
    }

    public void ShowBatchContent(UIElement content, string step = ImageAnalysisLiterarySteps.Image)
    {
        IsBatch = true; SetStep(step); SelectedImageCard.Visibility = Visibility.Collapsed;
        foreach (UIElement child in ((Grid)CenterPanel.Child).Children) child.Visibility = Visibility.Collapsed;
        _batchOverlay!.Content = content; _batchOverlay.Visibility = Visibility.Visible;
        _batchOutputOptions!.Visibility = Visibility.Collapsed;
        FooterStatusText.Text = _localize("Batch.Independent");
        BackButton.Content = _localize("Batch.Back");
    }
    public void ShowBatchSettings(ImageAnalysisLiterarySession session, bool single)
    {
        IsBatch = true; _batchOverlay!.Visibility = Visibility.Collapsed;
        ShowSettings(session); SelectedImageCard.Visibility = Visibility.Collapsed;
        _batchOutputOptions!.Visibility = Visibility.Visible;
        _batchSingleOption!.Content = _localize("Batch.Single"); _batchSingleOption.IsChecked = single;
        _batchFolderOption!.Content = _localize("Batch.Folder"); _batchFolderOption.IsChecked = !single;
        ((TextBlock)_batchOutputOptions.Children[1]).Text = _localize("Batch.CustomHint");
        SetComboByTag(FormComboBox, ImageAnalysisTextForms.WithTitle);
        FormComboBox.IsEnabled = false;
        FormComboBox.ToolTip = _localize("Batch.Headings");
        BackButton.Content = _localize("Batch.BackImages");
    }
    public void ShowBatchReport(ImageAnalysisLiterarySession session)
    {
        _session = session;
        ResultPanelTitleText.Text = _localize("Batch.Report");
        ReviewSummaryFooterText.Text = _localize("Batch.Independent");
        FindingsEmptyText.Visibility = Visibility.Collapsed; UncertaintiesPanel.Visibility = Visibility.Collapsed;
        FindingsItemsPanel.Children.Clear();
        FindingsItemsPanel.Children.Add(new TextBlock { Text = string.Join("\n", session.ReviewSummary.Items), TextWrapping = TextWrapping.Wrap });
    }
    public void EndBatchView()
    {
        IsBatch = false;
        FormComboBox.IsEnabled = true; FormComboBox.ToolTip = null;
        ApplyLocalization();
        if (_batchOverlay is not null) { _batchOverlay.Visibility = Visibility.Collapsed; _batchOverlay.Content = null; }
        if (_batchOutputOptions is not null) _batchOutputOptions.Visibility = Visibility.Collapsed;
        if (_batchFooterProgress is not null) _batchFooterProgress.Visibility = Visibility.Collapsed;
    }
    public void ReportBatchProgress(ImageBatchProgress p)
    {
        int percent = p.Total == 0 ? 0 : p.Finished * 100 / p.Total;
        _batchFooterProgress!.Visibility = Visibility.Visible; _batchFooterProgress.Value = percent;
        FooterStatusText.Text = _format("Batch.Progress", [p.Finished, p.Total, percent, p.Failed]) + " · " + _localize("Batch.Stage." + p.Stage);
    }
}
