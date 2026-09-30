using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace AIHub;

public sealed class ApplicationCloseChoiceWindow : Window
{
    private readonly CheckBox _remember = new();
    public bool? CloseToTray { get; private set; }
    public bool RememberChoice => _remember.IsChecked == true;

    public ApplicationCloseChoiceWindow(Window owner, Func<string, string> text, bool trayAvailable)
    {
        Owner = owner; Title = text("CloseChoice.Title");
        Resources.MergedDictionaries.Add(owner.Resources);
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/AIHub;component/Controls/SettingsResources.xaml")
        });
        Width = Math.Min(620, SystemParameters.WorkArea.Width - 32);
        SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "PanelBrush"); SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        SetResourceReference(FontSizeProperty, "UiBodyFontSize");
        var panel = new StackPanel { Margin = new(24) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = text("CloseChoice.Question"), FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 12) });
        panel.Children.Add(new TextBlock { Text = text("CloseChoice.Help"), TextWrapping = TextWrapping.Wrap,
            Margin = new(0, 0, 0, 18) });
        _remember.Content = text("CloseChoice.Remember");
        AutomationProperties.SetAutomationId(_remember, "CloseChoice.Remember");
        panel.Children.Add(_remember);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 12, 0, 0) };
        Button Action(string label, string id, bool toTray)
        {
            var button = new Button { Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap },
                Margin = new(6, 6, 0, 0), IsEnabled = !toTray || trayAvailable };
            AutomationProperties.SetAutomationId(button, id);
            button.Click += (_, _) => { CloseToTray = toTray; DialogResult = true; };
            return button;
        }
        buttons.Children.Add(Action(text("Settings.Behavior.Exit"), "CloseChoice.Exit", false));
        buttons.Children.Add(Action(text("Settings.Behavior.Tray"), "CloseChoice.Tray", true));
        panel.Children.Add(buttons);
        if (!trayAvailable) panel.Children.Add(new TextBlock { Text = text("CloseChoice.TrayUnavailable"),
            TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 0) });
    }
}
