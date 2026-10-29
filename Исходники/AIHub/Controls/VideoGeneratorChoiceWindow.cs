using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services.VideoPreparation;

namespace AIHub.Controls;

/// <summary>Choose only a model; cancellation never changes the project.</summary>
public sealed class VideoGeneratorChoiceWindow : Window
{
    public string? SelectedModelId { get; private set; }
    public VideoGeneratorChoiceWindow(Window? owner, Func<string, string> localize, string? selected)
    {
        string T(string key) => localize("VideoPrep." + key);
        VideoReferenceWindowUi.Configure(this, owner, T("FinishChooseModel"));
        Width = 470; MinWidth = 320; MinHeight = 0; Height = double.NaN; SizeToContent = SizeToContent.Height;
        MaxHeight = SystemParameters.WorkArea.Height - 40; ShowInTaskbar = false; ResizeMode = ResizeMode.NoResize;
        AutomationProperties.SetAutomationId(this, "VideoPrep.ModelPicker");
        var page = new StackPanel { Margin = new Thickness(18) };
        var list = new System.Windows.Controls.ListBox { ItemsSource = VideoGeneratorPlan.Candidates, DisplayMemberPath = "Name", MinHeight = 100, MaxHeight = 220 };
        list.SetResourceReference(System.Windows.Controls.ListBox.BackgroundProperty, "PanelBrush");
        list.SetResourceReference(System.Windows.Controls.ListBox.ForegroundProperty, "TextPrimaryBrush");
        list.SetResourceReference(System.Windows.Controls.ListBox.BorderBrushProperty, "LineBrush");
        list.SelectedItem = VideoGeneratorPlan.Candidates.FirstOrDefault(c => c.Id == selected)
            ?? VideoGeneratorPlan.Candidates.FirstOrDefault(c => c.Id == VideoH3Catalog.ModelId);
        AutomationProperties.SetAutomationId(list, "VideoPrep.ModelPicker.List"); page.Children.Add(list);
        var buttons = new WrapPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var apply = VideoReferenceWindowUi.Button(T("ModelPickerApply"), "ModelPicker.Apply", () =>
        { if (list.SelectedItem is VideoGeneratorCandidate model) { SelectedModelId = model.Id; DialogResult = true; } }); apply.IsDefault = true;
        var cancel = VideoReferenceWindowUi.Button(T("OpenRouteCancel"), "ModelPicker.Cancel", () => DialogResult = false); cancel.IsCancel = true;
        buttons.Children.Add(apply); buttons.Children.Add(cancel); page.Children.Add(buttons); Content = page;
    }
}
