using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace AIHub.Controls;

internal class VectorDialog : Window
{
    protected readonly StackPanel Body = new();
    private readonly Func<string, string> _l;
    private readonly DockPanel _layout = new() { Margin = new(20) };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 4) };
    protected string T(string key) => _l("Vector." + key);
    internal VectorDialog(Window? owner, Func<string, string> l, string title, string id)
    {
        _l = l; Title = T(title); Owner = owner; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = Math.Min(560, SystemParameters.WorkArea.Width - 32); MinWidth = Math.Min(360, Width);
        MaxHeight = Math.Max(200, SystemParameters.WorkArea.Height - 32); SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.CanResize;
        if (owner is not null) Resources.MergedDictionaries.Add(owner.Resources);
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/SettingsResources.xaml", UriKind.Relative) });
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush"); SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        _error.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        AutomationProperties.SetAutomationId(this, id); Content = _layout;
    }
    protected void Finish(Func<bool> apply, string key = "Settings.Apply")
    {
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); _layout.Children.Add(footer);
        footer.Children.Add(_error);
        var row = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 10, 0, 0) };
        var yes = new Button { Content = T(key), IsDefault = true, Margin = new(0, 0, 8, 0), Padding = new(14, 8, 14, 8) };
        yes.SetResourceReference(StyleProperty, "SettingsPrimaryActionStyle");
        AutomationProperties.SetAutomationId(yes, "Vector.Dialog.Apply");
        yes.Click += (_, _) =>
        {
            try { if (apply()) DialogResult = true; }
            catch (System.IO.InvalidDataException e) { _error.Text = e.Message.StartsWith("Vector.", StringComparison.Ordinal) ? _l(e.Message) : T("Error.Settings"); }
        };
        var no = new Button { Content = T("Settings.Cancel"), IsCancel = true, Padding = new(14, 8, 14, 8) };
        AutomationProperties.SetAutomationId(no, "Vector.Dialog.Cancel");
        no.Click += (_, _) => DialogResult = false;
        row.Children.Add(yes); row.Children.Add(no); footer.Children.Add(row);
        _layout.Children.Add(new ScrollViewer { Content = Body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
    }
    protected void Note(string key) => Body.Children.Add(new TextBlock { Text = T(key), TextWrapping = TextWrapping.Wrap, Margin = new(0, 5, 0, 12) });
    protected TextBox Input(string label, string value, string id)
    {
        Body.Children.Add(new TextBlock { Text = T(label), TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 5) });
        var box = new TextBox { Text = value, Margin = new(0, 0, 0, 4) };
        AutomationProperties.SetAutomationId(box, "Vector." + id); AutomationProperties.SetName(box, T(label));
        Body.Children.Add(box); return box;
    }
    protected ComboBox Choose(string label, IEnumerable<string> ids, string current, string group, string id)
    {
        Body.Children.Add(new TextBlock { Text = T(label), Margin = new(0, 10, 0, 5) });
        var box = new ComboBox { ItemsSource = ids.Select(value => new Choice(value, T(group + "." + value))).ToArray(),
            DisplayMemberPath = nameof(Choice.Text), SelectedValuePath = nameof(Choice.Id), SelectedValue = current, Margin = new(0, 0, 0, 5) };
        AutomationProperties.SetAutomationId(box, "Vector." + id); AutomationProperties.SetName(box, T(label)); Body.Children.Add(box); return box;
    }
    protected static string Pick(ComboBox box) => (string?)box.SelectedValue ?? "auto";
    protected static int Number(TextBox box, string error = "Vector.Error.Settings") =>
        int.TryParse(box.Text.Trim(), out var n) && n > 0 ? n : throw new System.IO.InvalidDataException(error);
    private sealed record Choice(string Id, string Text);
}
