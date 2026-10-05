using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace AIHub.Controls;

internal static class MusicWishUi
{
    public static TextBlock Text(string text, bool heading = false) => new()
    { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = heading ? 18 : 13,
        FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal, Margin = new(0, 4, 0, 6) };
    public static Button Button(string text, string id, Action action)
    {
        var button = new Button { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
            Margin = new(0, 3, 4, 3), HorizontalContentAlignment = HorizontalAlignment.Center };
        AutomationProperties.SetAutomationId(button, id); button.Click += (_, _) => action(); return button;
    }
    public static CheckBox Check(string text, bool value, Action<bool> changed)
    {
        var box = new CheckBox { Content = Text(text), IsChecked = value, Margin = new(0, 2, 0, 2) };
        box.SetResourceReference(CheckBox.ForegroundProperty, "TextPrimaryBrush");
        box.Checked += (_, _) => changed(true); box.Unchecked += (_, _) => changed(false); return box;
    }
    public static void PrepareWindow(Window window, string title, string id, double width = 680)
    {
        window.Title = title; window.Width = width; window.Height = 720; window.MinWidth = 400; window.MinHeight = 350;
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        if (Application.Current?.MainWindow is { } main) window.Resources.MergedDictionaries.Add(main.Resources);
        window.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("/AIHub;component/Controls/SettingsResources.xaml", UriKind.Relative) });
        window.SetResourceReference(Window.BackgroundProperty, "WindowBackgroundBrush");
        window.SetResourceReference(Window.ForegroundProperty, "TextPrimaryBrush");
        AutomationProperties.SetAutomationId(window, id);
    }
    public static StackPanel Footer(Func<string, string> l, Action apply, Action cancel)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        row.Children.Add(Button(l("Music.Wishes.Apply"), "Music.Apply", apply));
        row.Children.Add(Button(l("Music.Wishes.Cancel"), "Music.Cancel", cancel)); return row;
    }
}
