using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using AIHub.Services;
using AIHub.Services.SongClip;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Orientation = System.Windows.Controls.Orientation;

namespace AIHub.Controls;

public sealed partial class SongClipControl
{
    private void Build()
    {
        var root = new Grid(); Content = root;
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new());
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel(); root.Children.Add(header);
        var title = Label("Title"); title.FontSize = 24; title.FontWeight = FontWeights.SemiBold;
        header.Children.Add(title);
        var projects = new DockPanel { LastChildFill = true, Margin = new(2, 0, 2, 6) };
        header.Children.Add(projects);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        projects.Children.Add(actions);
        actions.Children.Add(Button("New", () => { if (_busy || !Checkpoint()) return; _project = FreshProject(); LoadFields(); }, "New"));
        actions.Children.Add(Button("Projects", Manage, "Projects"));
        actions.Children.Add(Button("Pin", () => { if (_busy) return; _project.Pinned = true; Checkpoint(); _status.Text = _l("SongClip.Saved"); }, "Pin"));
        _name.Margin = new(8, 3, 3, 3); _name.VerticalContentAlignment = VerticalAlignment.Center;
        _name.ToolTip = _l("SongClip.ProjectName");
        AutomationProperties.SetAutomationId(_name, "SongClip.ProjectName");
        AutomationProperties.SetName(_name, _l("SongClip.ProjectName")); projects.Children.Add(_name);

        var workspace = new Grid();
        foreach (var width in new[] { 15d, 14d, 26d, 13d, 32d })
            workspace.ColumnDefinitions.Add(new() { Width = new(width, GridUnitType.Star) });
        workspace.ColumnDefinitions[3].MinWidth = 220;
        workspace.RowDefinitions.Add(new() { Height = new(4, GridUnitType.Star) });
        workspace.RowDefinitions.Add(new() { Height = GridLength.Auto });
        workspace.RowDefinitions.Add(new() { Height = new(5, GridUnitType.Star) });
        var viewport = new ScrollViewer { Content = workspace, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, CanContentScroll = false };
        AutomationProperties.SetAutomationId(viewport, "SongClip.WorkspaceScroll");
        Grid.SetRow(viewport, 1); root.Children.Add(viewport);
        // A bounded canvas keeps star rows/columns finite inside the scroll viewer.
        // Small windows scroll the same layout instead of clipping controls or stacking the sketch differently.
        viewport.SizeChanged += (_, _) => FitWorkspace();
        viewport.ScrollChanged += (_, e) => { if (e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0) FitWorkspace(); };
        void FitWorkspace()
        {
            var bodyFont = TryFindResource("UiBodyFontSize") is double size ? size : 14;
            var scale = Math.Clamp(bodyFont / 14, 1, 1.6);
            workspace.Width = Math.Max(1280 * scale, viewport.ViewportWidth);
            workspace.Height = Math.Max(580, viewport.ViewportHeight);
            workspace.ColumnDefinitions[3].MinWidth = 220 * scale;
        }

        Multiline(_lyrics, "Lyrics"); Multiline(_timings, "Timings"); Multiline(_prompt, "Prompt");
        Place(WorkspacePanel("Lyrics", WithHeading("Lyrics", _lyrics)), 0, 0);
        Place(BuildReferencePanel(), 1, 0);
        Place(BuildParametersPanel(), 2, 0, 3);
        Place(Button("ImportSong", () => _ = ImportSong(), "ImportSong"), 0, 1);
        Place(BuildReferenceActions(), 1, 1);
        Place(BuildModelSelection(), 2, 1);
        Place(BuildGenerationActions(), 3, 1);
        var timingHeader = new StackPanel(); timingHeader.Children.Add(Label("Timings"));
        timingHeader.Children.Add(Button("ImportLrc", ImportLrc, "ImportLrc"));
        var timings = new DockPanel(); DockPanel.SetDock(timingHeader, Dock.Top);
        timings.Children.Add(timingHeader); timings.Children.Add(_timings);
        Place(WorkspacePanel("Timings", timings), 0, 2);
        Place(BuildPreviewPanel(), 1, 2, 2);
        _results.SetResourceReference(StyleProperty, "SongClipResults");
        AutomationProperties.SetAutomationId(_results, "SongClip.Results");
        Place(WorkspacePanel("Results", WithHeading("Results", _results)), 3, 2);
        Place(BuildOutputPanel(), 4, 1, 1, 2);

        var navigation = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(2, 6, 2, 0) };
        navigation.Children.Add(Button("Back", () => { if (!_busy && Checkpoint()) BackRequested?.Invoke(); }, "Back"));
        navigation.Children.Add(Button("Home", () => { if (Checkpoint()) HomeRequested?.Invoke(); }, "Home"));
        Grid.SetRow(navigation, 2); root.Children.Add(navigation);
        void Place(FrameworkElement element, int column, int row, int columns = 1, int rows = 1)
        {
            Grid.SetColumn(element, column); Grid.SetRow(element, row);
            Grid.SetColumnSpan(element, columns); Grid.SetRowSpan(element, rows); workspace.Children.Add(element);
        }
    }

    private Border WorkspacePanel(string id, UIElement child)
    {
        var panel = Panel(child); AutomationProperties.SetAutomationId(panel, "SongClip.Layout." + id); return panel;
    }

    private DockPanel WithHeading(string key, UIElement child)
    {
        var panel = new DockPanel(); var heading = Label(key); heading.FontWeight = FontWeights.SemiBold;
        DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading); panel.Children.Add(child); return panel;
    }

    private FrameworkElement BuildModelSelection()
    {
        var panel = new DockPanel { Margin = new(4) };
        var heading = Label("Model"); DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        var download = Button("Components", OpenComponents, "Components");
        download.ToolTip = download.Content; download.Content = "↓"; download.Width = 42;
        AutomationProperties.SetName(download, _l("SongClip.Components"));
        DockPanel.SetDock(download, Dock.Right); panel.Children.Add(download);
        AutomationProperties.SetAutomationId(_model, "SongClip.Model");
        AutomationProperties.SetName(_model, _l("SongClip.Model"));
        _model.ToolTip = _l("SongClip.BundleSizeHint");
        _model.Padding=new(10,7,10,7); _model.HorizontalContentAlignment=System.Windows.HorizontalAlignment.Left;
        _model.Click+=(_,_)=>OpenModelPicker();
        _model.VerticalAlignment = VerticalAlignment.Center; panel.Children.Add(_model); return panel;
    }

    private FrameworkElement BuildGenerationActions()
    {
        var panel = new Grid { Margin = new(2), VerticalAlignment = VerticalAlignment.Center };
        panel.ColumnDefinitions.Add(new()); panel.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        panel.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _generate = Button("Start", () => _ = StartAsync(), "Generate");
        _generate.Background = new SolidColorBrush(Color.FromRgb(31, 134, 73)); _generate.Foreground = Brushes.White;
        _generate.Content = new TextBlock { Text = _l("SongClip.Start"), TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center, Foreground = Brushes.White };
        AutomationProperties.SetName(_generate, _l("SongClip.Start")); panel.Children.Add(_generate);
        _pause = Button("Pause", () => _ = PauseResume(), "Pause"); _pause.Content = "Ⅱ";
        _pause.Background = Brushes.DimGray; _pause.Foreground = Brushes.White; _pause.Padding = new(8); _pause.MinWidth = 36;
        _pause.ToolTip = _l("SongClip.Pause"); AutomationProperties.SetName(_pause, _l("SongClip.Pause"));
        Grid.SetColumn(_pause, 1); panel.Children.Add(_pause);
        _stop = Button("Stop", StopGeneration, "Stop");
        _stop.Content = "⛔"; _stop.Background = Brushes.Firebrick; _stop.Foreground = Brushes.White; _stop.Padding = new(8); _stop.MinWidth = 36;
        _stop.ToolTip = _l("SongClip.Stop"); Grid.SetColumn(_stop, 2); panel.Children.Add(_stop); return panel;
    }
}
