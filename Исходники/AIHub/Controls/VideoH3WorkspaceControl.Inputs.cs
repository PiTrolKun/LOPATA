using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Brushes = System.Windows.Media.Brushes;

namespace AIHub.Controls;

internal sealed partial class VideoH3WorkspaceControl
{
    private sealed record Choice(string Id, string Text) { public override string ToString() => Text; }
    private TextBlock Label(string text)
    {
        var b = MusicWishUi.Text(text); b.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        b.SetResourceReference(TextBlock.FontSizeProperty, "UiBodyFontSize"); return b;
    }
    private Button ActionButton(string label, string id, Action action)
    {
        var b = MusicWishUi.Button(T(label), "VideoH3.Workspace." + id, action); b.Padding = new Thickness(10, 7, 10, 7);
        b.SetResourceReference(StyleProperty, "SettingsActionButtonStyle");
        if (b.Content is TextBlock t) t.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Foreground") { Source = b });
        return b;
    }
    private Border Panel(UIElement child)
    {
        var b = new Border { Child = child, CornerRadius = new CornerRadius(8), Padding = new Thickness(10), BorderThickness = new Thickness(1) };
        b.SetResourceReference(Border.BackgroundProperty, "PanelBrush"); b.SetResourceReference(Border.BorderBrushProperty, "LineBrush"); return b;
    }
    private static ScrollViewer Scroll(UIElement child, string id)
    {
        var s = new ScrollViewer { Content = child, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        AutomationProperties.SetAutomationId(s, "VideoH3.Workspace." + id); return s;
    }
    private StackPanel Group(StackPanel parent, string key, bool expanded = false)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 3, 2) };
        var expander = new Expander { Header = Label(T(key)), Content = panel, IsExpanded = expanded, Margin = new Thickness(0, 1, 0, 2) };
        expander.SetResourceReference(ForegroundProperty, "TextPrimaryBrush"); parent.Children.Add(expander); return panel;
    }
    private ComboBox Pick(StackPanel p, string key, string value, IEnumerable<string> options, Action<string> set, bool literal = false)
    {
        var b = new ComboBox { MinHeight = 28, Padding = new Thickness(5, 3, 5, 3), HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch };
        foreach (var id in options) b.Items.Add(new Choice(id, literal ? id : T("H3Option." + id)));
        b.SelectedItem = b.Items.OfType<Choice>().FirstOrDefault(x => x.Id == value);
        AutomationProperties.SetAutomationId(b, "VideoH3.Workspace." + key); AutomationProperties.SetName(b, T(key));
        b.SelectionChanged += (_, _) => { if (!_building && b.SelectedItem is Choice c) { set(c.Id); Changed(); } }; InputRow(p, key, b); return b;
    }
    private void InputRow(StackPanel parent, string key, UIElement input)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(45, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = new GridLength(55, GridUnitType.Star) });
        var label = Label(T(key)); label.Margin = new Thickness(0, 0, 6, 0); label.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(label); Grid.SetColumn(input, 1); row.Children.Add(input); parent.Children.Add(row);
    }
    private void Preset(StackPanel p, string key, double value, double[] options, Action<double> set, string? zeroKey = null)
    {
        var values = options.Append(value).Distinct().Order().ToArray();
        var b = Pick(p, key, value.ToString(CultureInfo.InvariantCulture), values.Select(v => v.ToString(CultureInfo.InvariantCulture)),
            v => set(double.Parse(v, CultureInfo.InvariantCulture)), true);
        for (var i = 0; i < values.Length; i++)
            b.Items[i] = new Choice(values[i].ToString(CultureInfo.InvariantCulture), values[i] == 0 && zeroKey is not null ? T(zeroKey) : values[i].ToString("0.###", CultureInfo.CurrentCulture));
        b.SelectedItem = b.Items.OfType<Choice>().Single(c => c.Id == value.ToString(CultureInfo.InvariantCulture));
    }
    private void Check(StackPanel p, string key, bool value, Action<bool> set, bool enabled = true)
    {
        var b = new CheckBox { Content = Label(T(key)), IsChecked = value, IsEnabled = enabled, MinHeight = 28, Margin = new Thickness(0, 2, 0, 2) };
        b.SetResourceReference(ForegroundProperty, "TextPrimaryBrush"); AutomationProperties.SetAutomationId(b, "VideoH3.Workspace." + key);
        b.Checked += (_, _) => { set(true); Changed(); }; b.Unchecked += (_, _) => { set(false); Changed(); }; p.Children.Add(b);
    }
    private void Number(StackPanel p, string key, double value, double min, double max, Action<double> set, bool integer = false, string? draftId = null)
    {
        var id = draftId ?? key;
        var input = new ExampleTextInput(T(key), value.ToString(CultureInfo.InvariantCulture), "VideoH3.Workspace." + key);
        input.Input.Text = _drafts.GetValueOrDefault(id, value.ToString(CultureInfo.InvariantCulture));
        void Apply(bool notify)
        {
            var text = input.Input.Text; _drafts[id] = text;
            if (double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n)
                && n >= min && n <= max && (!integer || n == Math.Truncate(n)) && (!key.EndsWith("Width", StringComparison.Ordinal) && !key.EndsWith("Height", StringComparison.Ordinal) || n % 32 == 0))
            { _invalid.Remove(id); input.Input.SetResourceReference(BorderBrushProperty, "LineBrush"); set(n); }
            else { _invalid.Add(id); input.Input.BorderBrush = Brushes.IndianRed; }
            if (notify) Changed();
        }
        input.Input.Padding = new Thickness(5, 3, 5, 3);
        Apply(false); input.Input.TextChanged += (_, _) => Apply(true); InputRow(p, key, input);
    }
    private void TextField(StackPanel p, string key, string value, Action<string> set, bool multiline = false, string? draftId = null)
    {
        p.Children.Add(Label(T(key))); var id = draftId ?? key;
        var field = new ExampleTextInput(T(key), T(key + "Example"), "VideoH3.Workspace." + key, multiline);
        if (multiline) field.Input.Height = 180;
        field.Input.MaxLength = multiline ? 100000 : 180; field.Input.Text = _drafts.GetValueOrDefault(id, value);
        if (multiline) LiterarySpellChecking.Enable(field.Input, "en-US");
        field.Input.TextChanged += (_, _) =>
        {
            _drafts[id] = field.Input.Text; set(field.Input.Text);
            if (!multiline && (field.Input.Text.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || key == "H3ExportName" && string.IsNullOrWhiteSpace(field.Input.Text)))
            { _invalid.Add(id); field.Input.BorderBrush = Brushes.IndianRed; }
            else { _invalid.Remove(id); field.Input.SetResourceReference(BorderBrushProperty, "LineBrush"); }
            Changed();
        }; p.Children.Add(field);
    }
    private void Hint(StackPanel p, string key)
    {
        if (p.Children.Count > 0 && p.Children[p.Children.Count - 1] is FrameworkElement last) last.ToolTip = T(key);
    }
}
