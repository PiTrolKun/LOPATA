using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services.SpeechAssets;
using AIHub.Services.VideoEditor;
using UserControl = System.Windows.Controls.UserControl;
using TextBox = System.Windows.Controls.TextBox;
using CheckBox = System.Windows.Controls.CheckBox;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;

namespace AIHub.Controls.SpeechAssets;

internal sealed class SpeechAssetRowView : UserControl
{
    public SpeechAssetRowView() { DataContextChanged += (_, _) => Render(); Loaded += (_, _) => Render(); }
    internal void Render()
    {
        if (Tag is not SpeechAssetsControl host || DataContext is not SpeechAssetRow row) return;
        SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        var border = new Border { BorderThickness = new(4, 0, 0, 1), Padding = new(8), Margin = new(0, 4, 0, 4) };
        border.SetResourceReference(Border.BorderBrushProperty, "LineBrush"); Content = border;
        var grid = new Grid(); border.Child = grid;
        grid.ColumnDefinitions.Add(new() { Width = new(4, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new() { Width = new(6, GridUnitType.Star) });
        var textPanel = new StackPanel { Margin = new(0, 0, 12, 0) }; grid.Children.Add(textPanel);
        var label = new CheckBox { Content = row.Group, IsChecked = row.Included, IsEnabled = !host.IsWorking };
        label.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        label.Checked += (_, _) => { row.Included = true; host.Changed(); };
        label.Unchecked += (_, _) => { row.Included = false; host.Changed(); }; textPanel.Children.Add(label);
        if (!string.IsNullOrEmpty(row.LastError))
        {
            var error = new TextBlock { Text = string.Format(host.T("LineError"), row.FailedAttempts), ToolTip = row.LastError,
                TextWrapping = TextWrapping.Wrap, Margin = new(0, 3, 0, 3) };
            error.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush"); textPanel.Children.Add(error);
        }
        var text = new TextBox { Text = row.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 78, IsReadOnly = host.IsWorking };
        text.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        text.SetResourceReference(BorderBrushProperty, "LineBrush");
        AutomationProperties.SetAutomationId(text, "SpeechAssets.Text." + row.Id); textPanel.Children.Add(text);
        var name = new TextBox { Text = row.FileName, ToolTip = host.T("FileName"), Margin = new(0, 4, 0, 0), IsReadOnly = host.IsWorking };
        name.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        name.SetResourceReference(BorderBrushProperty, "LineBrush");
        textPanel.Children.Add(name); name.TextChanged += (_, _) => { row.FileName = name.Text; host.Changed(); };
        var actions = new StackPanel { Margin = new(0, 0, 12, 0) }; Grid.SetColumn(actions, 1); grid.Children.Add(actions);
        var state = new TextBlock { FontSize = 26, HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
        state.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        actions.Children.Add(state);
        void Appearance()
        {
            state.Text = row.Confirmed ? "V" : "−";
            border.SetResourceReference(Border.BackgroundProperty, row.Confirmed ? "WindowBackgroundBrush" : "PanelBrush");
            border.SetResourceReference(Border.BorderBrushProperty, row.Confirmed ? "LineBrush" : "AccentBrush");
            text.SetResourceReference(BackgroundProperty, row.Confirmed ? "WindowBackgroundBrush" : "PanelBrush");
            name.SetResourceReference(BackgroundProperty, row.Confirmed ? "WindowBackgroundBrush" : "PanelBrush");
        }
        void Theme(Button button)
        {
            button.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
            button.SetResourceReference(BackgroundProperty, "SecondaryButtonBackgroundBrush");
            button.SetResourceReference(BorderBrushProperty, "LineBrush");
        }
        void Button(string key, Action click)
        {
            var button = new Button { Content = host.T(key), Margin = new(2), Padding = new(8, 5, 8, 5), IsEnabled = !host.IsWorking };
            Theme(button);
            button.Click += (_, _) => { try { click(); host.Changed(); Appearance(); } catch (Exception e) { host.Report(e); } };
            actions.Children.Add(button);
        }
        Button("Confirm", () => row.Confirm());
        Button("Regenerate", () => _ = host.GenerateAsync(row.Id));
        Button("RestoreText", () => text.Text = row.SourceText);
        var order = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var offset in new[] { -1, 1 })
        {
            var move = new Button { Content = offset < 0 ? "↑" : "↓", Margin = new(2), Padding = new(12, 3, 12, 3),
                ToolTip = host.T(offset < 0 ? "MoveUp" : "MoveDown"), IsEnabled = !host.IsWorking };
            Theme(move); move.Click += (_, _) => host.MoveRow(row, offset); order.Children.Add(move);
        }
        actions.Children.Add(order);
        text.TextChanged += (_, _) => { row.Edit(text.Text); host.Changed(); Appearance(); };
        var takes = new WrapPanel(); Grid.SetColumn(takes, 2); grid.Children.Add(takes);
        var singleSelection = row.Takes.Count(t => t.Selected) == 1;
        foreach (var take in row.Takes)
        {
            var panel = new StackPanel { Width = 260, Margin = new(4) };
            var selected = new System.Windows.Controls.RadioButton { IsChecked = singleSelection && take.Selected, IsEnabled = !host.IsWorking,
                GroupName = "SpeechAssets.Take." + row.Id,
                Content = (VideoNarratorCatalog.Models.FirstOrDefault(m => m.Id == take.Voice.Id)?.Name ?? take.Voice.Id) + " · " + take.Voice.Options.Speaker,
                ToolTip = take.Text + "\n" + take.CreatedUtc.ToLocalTime(), FontSize = 11 };
            selected.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
            AutomationProperties.SetAutomationId(selected, "SpeechAssets.Take." + take.Id);
            if (take.Text != row.Text) selected.Content += " · " + host.T("Stale");
            selected.Checked += (_, _) => { row.SelectTake(take.Id); host.Changed(); Appearance(); };
            panel.Children.Add(selected);
            var player = new DockPanel(); var slider = new Slider { Minimum = 0, Maximum = Math.Max(.1, take.Seconds), Margin = new(5) };
            var play = new Button { Content = "▶", Padding = new(10, 5, 10, 5), ToolTip = host.T("Play") }; Theme(play);
            play.Click += (_, _) => { try { host.Play(take, slider); } catch (Exception e) { host.Report(e); } }; player.Children.Add(play);
            var stop = new Button { Content = "■", Padding = new(8, 5, 8, 5) }; Theme(stop); stop.Click += (_, _) => host.StopAudio(); player.Children.Add(stop);
            slider.ValueChanged += (_, _) => host.Seek(slider); player.Children.Add(slider); panel.Children.Add(player);
            var duration = new TextBlock { Text = TimeSpan.FromSeconds(take.Seconds).ToString(@"m\:ss"), FontSize = 11 };
            duration.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush"); panel.Children.Add(duration);
            takes.Children.Add(panel);
        }
        Appearance();
    }
}
