using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Brush = System.Windows.Media.Brush;

namespace AIHub.Controls.OfficeTextEditor;

internal sealed partial class OfficeSymbolsWindow : Window
{
    private readonly Func<string, string> _l;
    private readonly TextBox _selection = new() { FontSize = 24, MinHeight = 42, VerticalContentAlignment = VerticalAlignment.Center,
        FontFamily = new("Segoe UI Emoji, Segoe UI Symbol, Segoe UI"), Margin = new(0, 5, 0, 8) };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _copy;

    internal OfficeSymbolsWindow(Window owner, Func<string, string> localize, ResourceDictionary theme, Brush background, Brush foreground)
    {
        _l = localize; Owner = owner; Icon = owner.Icon; Title = T("Title");
        Width = 980; Height = 650; MinWidth = 700; MinHeight = 480;
        FontSize = 14;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = background; Foreground = foreground;
        Resources.MergedDictionaries.Add(theme);
        Resources.MergedDictionaries.Add(new() { Source = new Uri("/AIHub;component/Controls/OfficeReader/ReaderResources.xaml", UriKind.Relative) });
        Resources.MergedDictionaries.Add(new() { Source = new Uri("/AIHub;component/Controls/OfficeReader/OfficeSummaryResources.xaml", UriKind.Relative) });
        AutomationProperties.SetAutomationId(this, "Office.Symbols.Window");
        var root = new DockPanel { Margin = new(18) }; Content = root;
        var bottom = new StackPanel { Margin = new(0, 12, 0, 0) }; DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        bottom.Children.Add(new TextBlock { Text = T("Selected") });
        AutomationProperties.SetAutomationId(_selection, "Office.Symbols.Selection");
        bottom.Children.Add(_selection);
        var actions = new DockPanel(); bottom.Children.Add(actions);
        var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        DockPanel.SetDock(buttons, Dock.Right); actions.Children.Add(buttons); actions.Children.Add(_status);
        Button Action(string key, Action action)
        {
            var button = new Button { Content = T(key), Margin = new(4, 0, 0, 0), Padding = new(12, 6, 12, 6) };
            AutomationProperties.SetAutomationId(button, "Office.Symbols." + key);
            button.Click += (_, _) => action(); buttons.Children.Add(button); return button;
        }
        _copy = Action("Copy", Copy); _copy.IsEnabled = false;
        Action("Clear", () => { _selection.Clear(); _selection.Focus(); });
        Action("Close", Close);
        _selection.TextChanged += (_, _) => { _copy.IsEnabled = _selection.Text.Length > 0; _status.Text = ""; _symbols.SetSelection(_selection.Text); };
        BuildLibrary(root);
    }

    private string T(string key) => _l("OfficeSymbols." + key);
    private void Copy()
    {
        var value = _selection.SelectionLength > 0 ? _selection.SelectedText : _selection.Text;
        if (value.Length == 0) return;
        try { System.Windows.Clipboard.SetText(value); _status.Text = T("Copied"); }
        catch (ExternalException) { _status.Text = T("CopyFailed"); }
    }
}
