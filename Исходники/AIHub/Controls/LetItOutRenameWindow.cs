using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls;

internal sealed class LetItOutRenameWindow : Window
{
    private readonly TextBox _name;
    internal string TopicTitle => _name.Text.Trim();
    internal LetItOutRenameWindow(Window? owner, Func<string, string> localize, string title)
    {
        Owner = owner; Title = localize("LetItOut.Library.Rename"); Width = 460;
        SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "PanelBrush"); SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        if (owner is not null) Resources.MergedDictionaries.Add(owner.Resources);
        var body = new StackPanel { Margin = new(22) };
        body.Children.Add(new TextBlock { Text = localize("LetItOut.Library.TopicName"), Margin = new(0, 0, 0, 10) });
        _name = new TextBox { Text = title, MaxLength = 160, FontSize = 16, Padding = new(10), MinWidth = 300 };
        AutomationProperties.SetAutomationId(_name, "LetItOut.Rename.Input");
        body.Children.Add(_name);
        var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new(0, 18, 0, 0) };
        var save = new Button { Content = localize("LetItOut.Library.Save"), IsDefault = true, Padding = new(16, 8, 16, 8), Margin = new(4) };
        save.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
        AutomationProperties.SetAutomationId(save, "LetItOut.Rename.Save");
        save.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(TopicTitle)) DialogResult = true; };
        _name.TextChanged += (_, _) => save.IsEnabled = !string.IsNullOrWhiteSpace(_name.Text);
        var cancel = new Button { Content = localize("LetItOut.Library.Cancel"), IsCancel = true, Padding = new(16, 8, 16, 8), Margin = new(4) };
        cancel.SetResourceReference(StyleProperty, "SecondaryButtonStyle"); actions.Children.Add(cancel); actions.Children.Add(save);
        body.Children.Add(actions); Content = body;
        Loaded += (_, _) => { _name.Focus(); _name.SelectAll(); };
    }
}
