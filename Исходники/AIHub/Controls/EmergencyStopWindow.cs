using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using Panel = System.Windows.Controls.Panel;

namespace AIHub.Controls;

/// <summary>A review-only dialog: it never cancels work or owns processes.</summary>
public sealed class EmergencyStopWindow : Window
{
    public EmergencyStopWindow(Func<string, string> localize, IReadOnlyList<string> tasks, IReadOnlyList<string> workers)
    {
        MusicWishUi.PrepareWindow(this, localize("EmergencyStop.Title"), "EmergencyStop.Window", 700);
        Height = Math.Min(600, SystemParameters.WorkArea.Height - 70);
        var root = new Grid { Margin = new(20) };
        root.RowDefinitions.Add(new());
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Content = root;

        var body = new StackPanel();
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new(0, 0, 0, 12) };
        root.Children.Add(scroll);
        var intro = MusicWishUi.Text(localize("EmergencyStop.Intro"), true);
        body.Children.Add(intro);
        body.Children.Add(MusicWishUi.Text(localize("EmergencyStop.Preserve")));
        AddList(body, localize("EmergencyStop.Tasks"), tasks, localize("EmergencyStop.NoTasks"), "EmergencyStop.Tasks");
        AddList(body, localize("EmergencyStop.Workers"), workers, localize("EmergencyStop.NoWorkers"), "EmergencyStop.Workers");

        var footer = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetRow(footer, 1); root.Children.Add(footer);
        var cancel = MusicWishUi.Button(localize("EmergencyStop.Cancel"), "EmergencyStop.Cancel", () => DialogResult = false);
        cancel.IsCancel = true; cancel.IsDefault = true;
        cancel.MinWidth = 110; cancel.Padding = new(14, 10, 14, 10);
        footer.Children.Add(cancel);
        var confirm = MusicWishUi.Button(localize("EmergencyStop.Confirm"), "EmergencyStop.Confirm", () => DialogResult = true);
        confirm.Background = new SolidColorBrush(Color.FromRgb(183, 28, 28));
        confirm.Foreground = Brushes.White;
        confirm.BorderBrush = Brushes.Firebrick;
        confirm.Margin = new(8, 3, 0, 3); confirm.Padding = new(14, 10, 14, 10);
        // TextBlock has its own inherited resource style; retain contrast in both themes.
        if (confirm.Content is TextBlock confirmText) confirmText.Foreground = Brushes.White;
        footer.Children.Add(confirm);
    }

    private static void AddList(Panel target, string heading, IReadOnlyList<string> entries, string empty, string id)
    {
        var title = MusicWishUi.Text(heading, true); title.Margin = new(0, 16, 0, 5); target.Children.Add(title);
        var items = new StackPanel { Margin = new(10, 6, 10, 6) };
        AutomationProperties.SetAutomationId(items, id);
        foreach (var entry in entries.Count == 0 ? new[] { empty } : entries)
        {
            var text = MusicWishUi.Text(entry); text.Margin = new(0, 3, 0, 3); items.Children.Add(text);
        }
        var panel = new Border { Child = items, BorderThickness = new(1), CornerRadius = new(6) };
        panel.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        panel.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        target.Children.Add(panel);
    }
}
