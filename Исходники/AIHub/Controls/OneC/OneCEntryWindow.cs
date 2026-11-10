using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MenuItem = System.Windows.Controls.MenuItem;
using ContextMenu = System.Windows.Controls.ContextMenu;
using Control = System.Windows.Controls.Control;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Panel = System.Windows.Controls.Panel;
using CheckBox = System.Windows.Controls.CheckBox;
using ProgressBar = System.Windows.Controls.ProgressBar;
using TabControl = System.Windows.Controls.TabControl;
using ComboBox = System.Windows.Controls.ComboBox;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services.OneC;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Orientation = System.Windows.Controls.Orientation;

namespace AIHub.Controls.OneC;

internal sealed class OneCEntryWindow : Window
{
    private readonly TextBox _name = new(), _message = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90 };
    private readonly OneCCodeEditor? _code;
    internal string EntryName => _name.Text.Trim();
    internal string Code => _code?.Text ?? "";
    internal string Message => _message.Text.Trim();
    internal OneCEntryWindow(Window owner, Func<string, string> localize, string title, string name,
        bool withCode = false, bool feedback = false, bool requireCode = false)
    {
        string T(string key) => localize("OneC." + key);
        Owner = owner; Icon = owner.Icon; Title = title; Width = withCode || feedback ? 900 : 500;
        Height = withCode || feedback ? 650 : 230; MinWidth = 360; MinHeight = withCode || feedback ? 400 : 200;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Resources.MergedDictionaries.Add(owner.Resources);
        Resources.MergedDictionaries.Add(new() { Source = new Uri("/AIHub;component/Controls/OneC/OneCResources.xaml", UriKind.Relative) });
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush"); SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        AutomationProperties.SetAutomationId(this, "OneC.EntryWindow");
        var body = new DockPanel { Margin = new(16) }; Content = body;
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom); body.Children.Add(bottom);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 6) }; bottom.Children.Add(error);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; bottom.Children.Add(actions);
        var accept = new Button { Content = T("Continue"), IsDefault = true, Padding = new(12, 6, 12, 6), Margin = new(3) };
        accept.Click += (_, _) =>
        {
            try
            {
                if (feedback && Message.Length == 0) throw new System.IO.InvalidDataException("OneC.PasteError");
                if (!feedback) _ = OneCProjectStore.Name(EntryName);
                if (requireCode && Code.Length == 0 || Code.Length > 2_000_000 || Message.Length > 20000)
                    throw new System.IO.InvalidDataException("OneC.InputLimit");
                DialogResult = true;
            }
            catch (System.IO.InvalidDataException e) { error.Text = localize(e.Message); }
        };
        AutomationProperties.SetAutomationId(accept, "OneC.EntryAccept"); actions.Children.Add(accept);
        actions.Children.Add(new Button { Content = T("Cancel"), IsCancel = true, Padding = new(12, 6, 12, 6), Margin = new(3) });
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); body.Children.Add(top);
        if (feedback)
        {
            top.Children.Add(new TextBlock { Text = T("PasteError"), Margin = new(0, 0, 0, 6) }); top.Children.Add(_message);
            AutomationProperties.SetAutomationId(_message, "OneC.FeedbackError");
            top.Children.Add(new TextBlock { Text = T("TriedCodeOptional"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 6) });
        }
        else
        {
            top.Children.Add(new TextBlock { Text = T("Name"), Margin = new(0, 0, 0, 6) }); _name.Text = name; top.Children.Add(_name);
            AutomationProperties.SetAutomationId(_name, "OneC.EntryName");
        }
        if (withCode || feedback)
        {
            _code = new(localize, "OneC.EntryCode") { Margin = new(0, 12, 0, 0) }; body.Children.Add(_code);
        }
        Loaded += (_, _) => { if (feedback) _message.Focus(); else { _name.Focus(); _name.SelectAll(); } };
    }
}
