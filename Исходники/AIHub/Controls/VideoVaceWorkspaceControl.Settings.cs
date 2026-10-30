using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services.VideoPreparation;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;

namespace AIHub.Controls;

internal sealed partial class VideoVaceWorkspaceControl
{
    private sealed record Choice(string Id, string Text) { public override string ToString() => Text; }
    private Button _exportVideo = new(), _sendEditor = new();
    private TextBlock _resultInfo = new();
    private void BuildSettings()
    {
        var basic = Group(_settings, "Basic", true);
        Preset(basic, "TargetSeconds", S.TargetSeconds, [0, 15, 30, 60, 120, 300, 600, 1800, 3600], v => S.TargetSeconds = v, "ByProject");
        Pick(basic, "Resolution", S.Width + "x" + S.Height, [new("832x480", T("Landscape")), new("480x832", T("Portrait"))], value =>
        { var size = value.Split('x'); S.Width = int.Parse(size[0], CultureInfo.InvariantCulture); S.Height = int.Parse(size[1], CultureInfo.InvariantCulture); });
        Hint(basic, "NativeFormatHint");
        Preset(basic, "Steps", S.Steps, [8, 12, 16, 20, 24, 32, 40, 50], v => S.Steps = (int)v);
        Number(basic, "Seed", S.Seed, -1, 9007199254740991, value => S.Seed = (long)value);
        Preset(basic, "RetryCount", S.RetryCount, [1, 2, 3, 4, 5], v => S.RetryCount = (int)v);
        var continuity = Group(_settings, "Continuity");
        Preset(continuity, "ContextFrames", S.ContextFrames, [5, 9, 13, 17, 25], v => S.ContextFrames = (int)v); Hint(continuity, "ContextHint");
        var memory = Group(_settings, "Memory");
        Pick(memory, "MemoryMode", S.MemoryMode, [new("auto", T("MemoryAuto")), new("gpu", T("MemoryGpu")), new("offload", T("MemoryOffload")), new("cpu", T("MemoryCpu"))], v => S.MemoryMode = v);
        Preset(memory, "VramReserve", S.VramReserveGb, [0, 1, 2, 3, 4, 6, 8], v => S.VramReserveGb = v);
    }
    private void BuildFiles()
    {
        var title = Label(T("ProjectFiles")); title.FontWeight = FontWeights.SemiBold; _files.Children.Add(title);
        _files.Children.Add(Label(_project.Title)); _files.Children.Add(Label(T("ProjectHint")));
        _files.Children.Add(Label(T("ExportName")));
        var field = new ExampleTextInput(T("ExportName"), T("ExportNameExample"), "VideoVace.Workspace.ExportName");
        field.Input.MaxLength = 180; field.Input.Text = _drafts.GetValueOrDefault("ExportName", S.ExportName);
        field.Input.TextChanged += (_, _) =>
        {
            S.ExportName = field.Input.Text; _drafts["ExportName"] = field.Input.Text;
            if (string.IsNullOrWhiteSpace(S.ExportName) || S.ExportName.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
            { _invalid.Add("ExportName"); field.Input.BorderBrush = System.Windows.Media.Brushes.IndianRed; }
            else { _invalid.Remove("ExportName"); field.Input.SetResourceReference(BorderBrushProperty, "LineBrush"); }
            Changed();
        }; if (string.IsNullOrWhiteSpace(field.Input.Text) || field.Input.Text.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0) _invalid.Add("ExportName");
        _files.Children.Add(field);
        var percent = VideoH3Quality.ToPercent(S.Crf);
        Pick(_files, "Quality", percent.ToString(CultureInfo.InvariantCulture), new[] { 40, 60, 80, 85, 90, 95, 100, percent }.Distinct().Order()
            .Select(v => new Choice(v.ToString(CultureInfo.InvariantCulture), v + "%")), value => S.Crf = VideoH3Quality.ToCrf(int.Parse(value, CultureInfo.InvariantCulture)));
        Preset(_files, "OutputHeight", S.OutputHeight, [0, 720, 1080], value => S.OutputHeight = (int)value, "AsGenerated");
        _files.Children.Add(Label(T("NewResultsHint")));
        _files.Children.Add(ActionButton("Save", "Save", () => Request(SaveRequested)));
        _files.Children.Add(ActionButton("SaveAs", "SaveAs", () => Request(SaveAsRequested)));
        _files.Children.Add(ActionButton("ExportPreparation", "ExportPreparation", () => Request(ExportRequested)));
        _exportVideo = ActionButton("ExportVideo", "ExportVideo", () => _ = ChooseVideoExportAsync()); _files.Children.Add(_exportVideo);
        _sendEditor = ActionButton("SendEditor", "SendEditor", () => { if (!IsBusy && TryCommit() && SelectedVideo is { } path) SendEditorRequested?.Invoke(path); }); _files.Children.Add(_sendEditor);
        _resultInfo = Label(""); AutomationProperties.SetAutomationId(_resultInfo, "VideoVace.Workspace.ResultInfo"); _files.Children.Add(_resultInfo);
    }
    private TextBlock Label(string value)
    {
        var block = MusicWishUi.Text(value); block.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        block.SetResourceReference(TextBlock.FontSizeProperty, "UiBodyFontSize"); return block;
    }
    private Button ActionButton(string key, string id, Action action)
    {
        var button = MusicWishUi.Button(T(key), "VideoVace.Workspace." + id, action); button.Padding = new Thickness(10, 7, 10, 7);
        button.SetResourceReference(StyleProperty, "SettingsActionButtonStyle"); AutomationProperties.SetName(button, T(key));
        if (button.Content is TextBlock label) label.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Foreground") { Source = button });
        return button;
    }
    private Border Panel(UIElement child)
    {
        var panel = new Border { Child = child, CornerRadius = new CornerRadius(8), Padding = new Thickness(10), BorderThickness = new Thickness(1) };
        panel.SetResourceReference(Border.BackgroundProperty, "PanelBrush"); panel.SetResourceReference(Border.BorderBrushProperty, "LineBrush"); return panel;
    }
    private static ScrollViewer Scroll(UIElement child, string id)
    {
        var scroll = new ScrollViewer { Content = child, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        AutomationProperties.SetAutomationId(scroll, "VideoVace.Workspace." + id); return scroll;
    }
    private StackPanel Group(StackPanel parent, string key, bool expanded = false)
    {
        var content = new StackPanel { Margin = new Thickness(0, 0, 3, 2) };
        var expander = new Expander { Header = Label(T(key)), Content = content, IsExpanded = expanded, Margin = new Thickness(0, 1, 0, 2) };
        expander.SetResourceReference(ForegroundProperty, "TextPrimaryBrush"); parent.Children.Add(expander); return content;
    }
    private void InputRow(StackPanel parent, string key, UIElement input)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(45, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = new GridLength(55, GridUnitType.Star) });
        var label = Label(T(key)); label.Margin = new Thickness(0, 0, 6, 0); label.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(label); Grid.SetColumn(input, 1); row.Children.Add(input); parent.Children.Add(row);
    }
    private ComboBox Pick(StackPanel parent, string key, string selected, IEnumerable<Choice> choices, Action<string> apply)
    {
        var combo = new ComboBox { MinHeight = 28, Padding = new Thickness(5, 3, 5, 3), HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch };
        foreach (var choice in choices) combo.Items.Add(choice);
        combo.SelectedItem = combo.Items.OfType<Choice>().FirstOrDefault(c => c.Id == selected);
        AutomationProperties.SetAutomationId(combo, "VideoVace.Workspace." + key); AutomationProperties.SetName(combo, T(key));
        combo.SelectionChanged += (_, _) => { if (!_building && combo.SelectedItem is Choice choice) { apply(choice.Id); Changed(); } };
        InputRow(parent, key, combo); return combo;
    }
    private void Preset(StackPanel parent, string key, double selected, double[] values, Action<double> apply, string? zeroKey = null) =>
        Pick(parent, key, selected.ToString(CultureInfo.InvariantCulture), values.Append(selected).Distinct().Order().Select(value =>
            new Choice(value.ToString(CultureInfo.InvariantCulture), value == 0 && zeroKey is not null ? T(zeroKey) : value.ToString("0.###", CultureInfo.CurrentCulture))),
            value => apply(double.Parse(value, CultureInfo.InvariantCulture)));
    private void Number(StackPanel parent, string key, long selected, long minimum, long maximum, Action<long> apply)
    {
        var field = new ExampleTextInput(T(key), selected.ToString(CultureInfo.InvariantCulture), "VideoVace.Workspace." + key);
        field.Input.Text = _drafts.GetValueOrDefault(key, selected.ToString(CultureInfo.InvariantCulture)); field.Input.Padding = new Thickness(5, 3, 5, 3);
        if (!long.TryParse(field.Input.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var original) || original < minimum || original > maximum) _invalid.Add(key);
        field.Input.TextChanged += (_, _) =>
        {
            _drafts[key] = field.Input.Text;
            if (long.TryParse(field.Input.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= minimum && value <= maximum)
            { _invalid.Remove(key); field.Input.SetResourceReference(BorderBrushProperty, "LineBrush"); apply(value); }
            else { _invalid.Add(key); field.Input.BorderBrush = System.Windows.Media.Brushes.IndianRed; }
            Changed();
        }; InputRow(parent, key, field);
    }
    private void Hint(StackPanel parent, string key)
    { if (parent.Children.Count > 0 && parent.Children[parent.Children.Count - 1] is FrameworkElement last) last.ToolTip = T(key); }
}
