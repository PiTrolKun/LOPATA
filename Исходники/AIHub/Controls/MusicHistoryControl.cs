using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using AIHub.Models;
using Button = System.Windows.Controls.Button;

namespace AIHub.Controls;

public sealed class MusicHistoryControl : Grid
{
    private readonly MusicProjectController _projects;
    private readonly Button _mode, _previous, _next;
    public MusicHistoryControl(MusicProjectController projects)
    {
        _projects = projects; ColumnDefinitions.Add(new() { Width = GridLength.Auto }); ColumnDefinitions.Add(new());
        ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _mode = Make("Mode", () => { projects.CycleMode(); }); _previous = Make("Previous", () => projects.Navigate(-1));
        _next = Make("Next", () => projects.Navigate(1));
        Children.Add(_mode); var arrows = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        arrows.Children.Add(_previous); arrows.Children.Add(_next); SetColumn(arrows, 2); Children.Add(arrows);
        _previous.Content = "‹"; _next.Content = "›";
        Refresh();
    }
    private static Button Make(string suffix, Action action)
    {
        var button = new Button { Width = 24, Height = 24, MinWidth = 0, MinHeight = 0, Padding = new(0), FontSize = 15, Margin = new(1, 0, 1, 0) };
        AutomationProperties.SetAutomationId(button, "Music.History." + suffix); button.Click += (_, _) => action(); return button;
    }
    public void Refresh()
    {
        _mode.Content = _projects.Mode switch { MusicHistoryMode.Text => "Т", MusicHistoryMode.Settings => "⚙", _ => "○" };
        MusicAudioUi.Label(_mode, _projects.Text("Mode" + _projects.Mode) + "\n" + _projects.HistoryHint);
        MusicAudioUi.Label(_previous, _projects.Text("Previous") + "\n" + _projects.HistoryHint);
        MusicAudioUi.Label(_next, _projects.Text("Next") + "\n" + _projects.HistoryHint);
        _previous.IsEnabled = _projects.CanNavigate(-1); _next.IsEnabled = _projects.CanNavigate(1);
        _mode.IsEnabled = !_projects.Busy;
    }
}
