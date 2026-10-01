using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace AIHub;

/// <summary>A short explanation. Closing has no navigation or model side effects.</summary>
public sealed class ScenarioTagDescriptionWindow : Window
{
    public ScenarioTagDescriptionWindow(Window owner, ScenarioNavigationTag tag, Func<string, string> text)
    {
        Owner = owner; Title = text(tag.TitleKey); ShowInTaskbar = false;
        Resources.MergedDictionaries.Add(owner.Resources);
        Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("/AIHub;component/Controls/SettingsResources.xaml", UriKind.Relative) });
        Width = Math.Min(640, SystemParameters.WorkArea.Width - 32);
        MaxHeight = Math.Max(240, SystemParameters.WorkArea.Height - 48);
        SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "PanelBrush");
        SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        AutomationProperties.SetAutomationId(this, "Cloud.DescriptionWindow");
        var panel = new StackPanel { Margin = new Thickness(24) };
        TextBlock Label(string value, string font)
        {
            var label = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
            label.SetResourceReference(TextBlock.FontSizeProperty, font);
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            return label;
        }
        panel.Children.Add(Label(text(tag.TitleKey), "UiSectionFontSize"));
        panel.Children.Add(Label(text(tag.DescriptionKey), "UiBodyFontSize"));
        var target = ScenarioNavigationCatalog.Get(tag.TargetId);
        panel.Children.Add(Label(text("Cloud.Destination") + " " + text(target.TitleKey), "UiBodyFontSize"));
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        Button Action(string key, string id)
        {
            var button = new Button { Content = new TextBlock { Text = text(key), TextWrapping = TextWrapping.Wrap },
                Margin = new Thickness(6, 6, 0, 0), Padding = new Thickness(16, 10, 16, 10) };
            button.SetResourceReference(FontSizeProperty, "UiBodyFontSize");
            AutomationProperties.SetAutomationId(button, id); actions.Children.Add(button); return button;
        }
        var close = Action("Cloud.CloseDescription", "Cloud.Description.Close");
        close.IsCancel = true; close.Click += (_, _) => DialogResult = false;
        var go = Action("Cloud.Go", "Cloud.Description.Go");
        go.IsEnabled = target.IsAvailable;
        go.SetResourceReference(BackgroundProperty, "AccentBrush");
        go.Foreground = System.Windows.Media.Brushes.White;
        go.Click += (_, _) => DialogResult = true;
        panel.Children.Add(actions);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }
}
