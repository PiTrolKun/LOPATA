using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace AIHub.Controls;

public sealed class MusicExampleParametersWindow : Window
{
    public MusicProjectSnapshot? Result { get; private set; }
    public MusicExampleParametersWindow(MusicExample example, MusicExampleMetadata? metadata, Func<string, string> l, bool canApply)
    {
        MusicWishUi.PrepareWindow(this, l("Music.Examples.Parameters") + " · " + example.DisplayTitle(l),
            "Music.Examples.Parameters." + example.Id, 780);
        Width = 780; Height = 740; MinWidth = 480; MinHeight = 420; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        AutomationProperties.SetAutomationId(this, "Music.Examples.Parameters." + example.Id);
        var grid = new Grid { Margin = new(18) };
        grid.RowDefinitions.Add(new()); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var body = new StackPanel();
        if (example.Cloud) {
            Field(l("Music.Examples.Request"), example.Request, "Music.Examples.Request");
            Field(l("Music.Examples.Genre"), example.Genre, "Music.Examples.Genre");
        }
        else if (metadata is not null) {
            Field(l("Music.Examples.Request"), metadata.Lyrics, "Music.Examples.Request");
            foreach (var tag in metadata.Tags.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)) {
                if (tag.Key.Equals("lyrics", StringComparison.OrdinalIgnoreCase) || tag.Key.StartsWith("lyrics-", StringComparison.OrdinalIgnoreCase)) continue;
                var key = "Music.Examples.Tag." + tag.Key.ToLowerInvariant();
                Field(l(key) == key ? tag.Key : l(key), tag.Value, "Music.Examples.Tag." + tag.Key, tag.Key);
            }
            var technical = new StackPanel();
            var raw = ReadOnly(metadata.Technical, "Music.Examples.Technical"); technical.Children.Add(raw);
            var expand = new Expander { Header = l("Music.Examples.Technical"), Content = technical, Margin = new(0, 8, 0, 8) };
            expand.SetResourceReference(ForegroundProperty, "TextPrimaryBrush"); body.Children.Add(expand);
        }
        grid.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 12, 0, 0) };
        if (!example.Cloud && metadata is not null) {
            var apply = new Button { Content = l("Music.Examples.Apply"), Padding = new(12, 8, 12, 8), IsEnabled = canApply };
            apply.ToolTip = l("Music.Examples.ApplyHint"); AutomationProperties.SetAutomationId(apply, "Music.Examples.Apply");
            apply.Click += (_, _) => {
                try { Result = metadata.Restore(); DialogResult = true; }
                catch (Exception error) when (error is System.IO.IOException or System.IO.InvalidDataException or System.Text.Json.JsonException or ArgumentException or FormatException or OverflowException) {
                    System.Windows.MessageBox.Show(this, l("Music.Examples.Error") + "\n" + error.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };
            actions.Children.Add(apply);
        }
        var close = new Button { Content = l("Music.Examples.Close"), IsCancel = true, Padding = new(12, 8, 12, 8), Margin = new(10, 0, 0, 0) };
        actions.Children.Add(close); Grid.SetRow(actions, 1); grid.Children.Add(actions); Content = grid;

        void Field(string label, string value, string id, string? technicalName = null)
        {
            var heading = MusicAudioUi.Text(14); heading.Text = label; heading.FontWeight = FontWeights.SemiBold;
            heading.Margin = new(0, 10, 0, 5); heading.ToolTip = technicalName; body.Children.Add(heading);
            body.Children.Add(ReadOnly(value, id));
        }
    }
    private static TextBox ReadOnly(string value, string id)
    {
        var box = new TextBox { Text = value, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Padding = new(8), FontSize = 14,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        box.SetResourceReference(BackgroundProperty, "SecondaryButtonBackgroundBrush"); box.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        box.SetResourceReference(BorderBrushProperty, "LineBrush"); AutomationProperties.SetAutomationId(box, id); return box;
    }
}
