using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Globalization;
using Binding = System.Windows.Data.Binding;
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
        Width = Math.Min(690, SystemParameters.WorkArea.Width - 40); MinWidth = 320; MinHeight = 0; Height = double.NaN; SizeToContent = SizeToContent.Height;
        MaxHeight = SystemParameters.WorkArea.Height - 40; ShowInTaskbar = false; ResizeMode = ResizeMode.NoResize;
        AutomationProperties.SetAutomationId(this, "VideoPrep.ModelPicker");
        var page = new StackPanel { Margin = new Thickness(18) };
        page.SetResourceReference(System.Windows.Controls.Panel.BackgroundProperty, "WindowBackgroundBrush");
        var list = new System.Windows.Controls.ListBox { ItemsSource = VideoGeneratorPlan.AvailableCandidates,
            ItemTemplate = ModelTemplate(T), MinHeight = 100, MaxHeight = Math.Max(100, Math.Min(280, SystemParameters.WorkArea.Height - 220)),
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        list.SetResourceReference(System.Windows.Controls.ListBox.BackgroundProperty, "PanelBrush");
        list.SetResourceReference(System.Windows.Controls.ListBox.ForegroundProperty, "TextPrimaryBrush");
        list.SetResourceReference(System.Windows.Controls.ListBox.BorderBrushProperty, "LineBrush");
        list.SelectedItem = VideoGeneratorPlan.AvailableCandidates.FirstOrDefault(c => c.Id == selected)
            ?? VideoGeneratorPlan.AvailableCandidates.FirstOrDefault(c => c.Id == VideoH3Catalog.ModelId);
        AutomationProperties.SetAutomationId(list, "VideoPrep.ModelPicker.List"); page.Children.Add(list);
        var hint = VideoReferenceWindowUi.Text(T("ModelMemoryHint"));
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush"); page.Children.Add(hint);
        var buttons = new WrapPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var apply = VideoReferenceWindowUi.Button(T("ModelPickerApply"), "ModelPicker.Apply", () =>
        { if (list.SelectedItem is VideoGeneratorCandidate model && VideoGeneratorPlan.IsAvailable(model.Id)) { SelectedModelId = model.Id; DialogResult = true; } }); apply.IsDefault = true;
        var cancel = VideoReferenceWindowUi.Button(T("OpenRouteCancel"), "ModelPicker.Cancel", () => DialogResult = false); cancel.IsCancel = true;
        buttons.Children.Add(apply); buttons.Children.Add(cancel); page.Children.Add(buttons); Content = page;
    }
    private static DataTemplate ModelTemplate(Func<string, string> text)
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel)); panel.SetValue(StackPanel.MarginProperty, new Thickness(6));
        var title = new FrameworkElementFactory(typeof(TextBlock)); title.SetBinding(TextBlock.TextProperty, new Binding("Name"));
        title.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold); title.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        panel.AppendChild(title);
        var memory = new FrameworkElementFactory(typeof(TextBlock));
        memory.SetBinding(TextBlock.TextProperty, new Binding("Id") { Converter = new MemoryText(text) });
        memory.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); memory.SetValue(TextBlock.MarginProperty, new Thickness(0, 5, 0, 0));
        panel.AppendChild(memory); return new DataTemplate { VisualTree = panel };
    }
    private sealed class MemoryText(Func<string, string> text) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => text((value as string) switch
        { VideoH3Catalog.ModelId => "ModelMemoryH3", VideoH3LiteCatalog.ModelId => "ModelMemoryH3Lite", _ => "ModelMemoryUnknown" });
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}
