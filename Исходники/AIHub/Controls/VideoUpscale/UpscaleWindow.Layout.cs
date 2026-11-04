using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using AIHub.Services;
using AIHub.Services.VideoEditor;
using AIHub.Services.VideoUpscale;
using AIHub.Controls.VideoEditor;
using Button = System.Windows.Controls.Button;
using Panel = System.Windows.Controls.Panel;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Brushes = System.Windows.Media.Brushes;

namespace AIHub.Controls.VideoUpscale;

internal sealed partial class UpscaleWindow
{
    private Window? _journalWindow;
    private UIElement Layout()
    {
        var root = new Grid { Margin = new(14), ClipToBounds = true };
        root.SetResourceReference(Panel.BackgroundProperty, "WindowBackgroundBrush");
        AutomationProperties.SetAutomationId(root, "VideoUpscale.Workspace");
        foreach (var height in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto })
            root.RowDefinitions.Add(new() { Height = height });
        var top = new StackPanel(); Place(root, top, 0); top.Children.Add(_toolbar);
        Add(_toolbar, "Import", () => Safe(Import)); Add(_toolbar, "OpenProject", () => Safe(OpenProject)); Add(_toolbar, "SaveAs", SaveAs);
        Add(_toolbar, "Mode", ChangeMode); Add(_toolbar, "Export", () => Safe(() => Process(false)));
        Add(_toolbar, "Cache", () => { var path = UpscaleStore.Work(_project.Id); Directory.CreateDirectory(path); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); });
        foreach (var button in _toolbar.Children.OfType<Button>().Where(b => b.Content is string)) button.Padding = new(7, 3, 7, 3);
        _sourceText.FontSize = 12; _sourceText.Margin = new(0, 2, 0, 5); top.Children.Add(_sourceText);

        var players = new Grid { Margin = new(0, 2, 0, 4), ClipToBounds = true };
        players.ColumnDefinitions.Add(new()); players.ColumnDefinitions.Add(new() { Width = new(8) }); players.ColumnDefinitions.Add(new());
        players.Children.Add(Player(T("Original"), _original)); var right = Player(T("Processed"), _result); Grid.SetColumn(right, 2); players.Children.Add(right); Place(root, players, 1);
        AutomationProperties.SetAutomationId(players, "VideoUpscale.Comparison");

        var playback = new StackPanel { Margin = new(0, 2, 0, 4) }; Place(root, playback, 2);
        var transport = new WrapPanel(); playback.Children.Add(transport);
        Add(transport, "Start", () => Safe(() => Seek(0))); Add(transport, "PreviousFrame", () => Safe(() => Seek(Math.Max(0, _at - 1 / _project.Media.Fps))));
        Add(transport, "Play", () => Safe(async () => { if (!_playing && _at >= _project.PreviewLength) await Seek(0); _playing = !_playing; await _original.PlayAsync(_playing); await _result.PlayAsync(_playing); }));
        Add(transport, "NextFrame", () => Safe(() => Seek(Math.Min(_project.PreviewLength, _at + 1 / _project.Media.Fps))));
        transport.Children.Add(_clock); _clock.FontSize = 13;
        Add(transport, "Fit", () => Safe(() => Zoom(1))); Add(transport, "Zoom2", () => Safe(() => Zoom(2))); Add(transport, "Zoom4", () => Safe(() => Zoom(4))); Add(transport, "Pan", ShowPan);
        _previewText.FontSize = 12; _previewText.MaxWidth = 170; _previewText.SetBinding(ToolTipProperty, new System.Windows.Data.Binding("Text") { Source = _previewText }); _previewText.VerticalAlignment = VerticalAlignment.Center; _previewText.Margin = new(10, 0, 0, 0); transport.Children.Add(_previewText);
        _position.Height = 16; playback.Children.Add(_position);
        AutomationProperties.SetAutomationId(playback, "VideoUpscale.Transport");

        Place(root, StepStrip("Software", _software), 3);
        var timeline = new Grid { Margin = new(0, 4, 0, 4) }; timeline.ColumnDefinitions.Add(new() { Width = new(112) }); timeline.ColumnDefinitions.Add(new());
        var track = new Border { Padding = new(10, 28, 6, 0), BorderThickness = new(1), Child = new TextBlock { Text = T("VideoTrack"), FontSize = 13, Foreground = Brushes.DeepSkyBlue } };
        track.SetResourceReference(Border.BackgroundProperty, "PanelBrush"); track.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        timeline.Children.Add(track); Grid.SetColumn(_range, 1); timeline.Children.Add(_range); Place(root, timeline, 4);
        AutomationProperties.SetAutomationId(timeline, "VideoUpscale.Timeline");

        var neural = new StackPanel(); Place(root, neural, 5); neural.Children.Add(StepStrip("AIShort", _ai));
        neural.Children.Add(_aiCommands);
        var range = new WrapPanel { Margin = new(0, 1, 0, 3) }; neural.Children.Add(range);
        _rangeText.FontSize = 12; _rangeText.VerticalAlignment = VerticalAlignment.Center; _rangeText.Margin = new(0, 0, 14, 0); range.Children.Add(_rangeText);
        var start = new TextBlock { Text = T("RangeStart"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center }; range.Children.Add(start);
        _rangeStart.Width = 160; _rangeStart.Height = 22; range.Children.Add(_rangeStart);
        range.Children.Add(new TextBlock { Text = T("RangeLength"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new(10, 0, 0, 0) });
        _rangeLength.Width = 110; _rangeLength.Height = 22; range.Children.Add(_rangeLength);

        var bottom = new StackPanel(); Place(root, bottom, 6); bottom.Children.Add(_progress);
        var operation = new DockPanel { Margin = new(0, 5, 0, 0) }; bottom.Children.Add(operation);
        DockPanel.SetDock(_actions, Dock.Left); operation.Children.Add(_actions);
        Add(_actions, "Pause", () => Safe(async () => { if (_busy && ApplicationBackgroundOperations.Current is {} host && host.State?.Kind is UpscalePipeline.Kind or UpscaleAssembly.PartialKind) await host.PauseAsync(); }));
        Add(_actions, "Resume", () => Safe(Resume)); Add(_actions, "Cancel", () => _operation?.Cancel());
        Add(_actions, "ExportAvailable", () => Safe(ExportAvailable)); Add(_actions, "Journal", ShowJournal);
        _status.FontSize = 12; _status.VerticalAlignment = VerticalAlignment.Center; _status.Margin = new(10, 0, 0, 0); _status.ToolTip = T("RecoveryHint"); operation.Children.Add(_status);
        return root;
    }
    private static void Place(Grid root, UIElement item, int row) { Grid.SetRow(item, row); root.Children.Add(item); }
    private static Grid Player(string title, UIElement player)
    {
        var grid = new Grid { ClipToBounds = true }; grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new());
        grid.Children.Add(new TextBlock { Text = title, FontSize = 13, Margin = new(0, 0, 0, 4) });
        var frame = new Border { Background = Brushes.Black, BorderThickness = new(1), Child = player, ClipToBounds = true }; frame.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        Grid.SetRow(frame, 1); grid.Children.Add(frame); return grid;
    }
    private UIElement StepStrip(string name, StackPanel steps)
    {
        var row = new DockPanel { Margin = new(0, 3, 0, 3) };
        var title = new TextBlock { Text = T(name), Width = 112, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        DockPanel.SetDock(title, Dock.Left); row.Children.Add(title);
        row.Children.Add(new ScrollViewer { Content = steps, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new(0) }); return row;
    }
    private Button Command(string key, Action action)
    {
        var button = new Button { Content = T(key), Padding = new(9, 3, 9, 3), MinHeight = 32, Margin = new(0, 0, 4, 3), ToolTip = T(key) };
        var iconKey = key switch { "Start" => "Beginning", "Fit" => "FrameFit", _ => key };
        if (VideoEditorCommands.Icon(iconKey, button) is {} icon) { button.Content = icon; button.Width = button.MinWidth = 36; button.Height = button.MinHeight = 34; button.Padding = new(7); }
        if (key == "Cache") {
            var folder = new System.Windows.Shapes.Path { Data = Geometry.Parse("M2,6 H9 L11,9 H22 V21 H2 Z M2,6 V3 H9 L11,6 H20 V9"), StrokeThickness = 1.65, StrokeLineJoin = PenLineJoin.Round };
            folder.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new System.Windows.Data.Binding("Foreground") { Source = button });
            button.Content = new Viewbox { Width = 19, Height = 19, Child = folder }; button.Width = button.Height = 36; button.Padding = new(7);
        }
        if (key == "Mode") { button.Content = "⚙"; button.FontSize = 18; button.Width = button.Height = 36; button.Padding = new(3); }
        AutomationProperties.SetName(button, T(key)); AutomationProperties.SetAutomationId(button, "VideoUpscale." + key);
        button.Click += (_, _) => Safe(() => { action(); return Task.CompletedTask; }); return button;
    }
    private void Add(Panel panel, string key, Action action) => panel.Children.Add(Command(key, action));
    private void ShowJournal()
    {
        if (_journalWindow is {} existing) { existing.Show(); existing.Activate(); return; }
        var window = new Window { Owner = this }; MusicWishUi.PrepareWindow(window, T("Journal"), "VideoUpscale.Journal", 800); window.Height = 400;
        _log.MaxHeight = double.PositiveInfinity; _log.Margin = new(10); window.Content = _log;
        window.Closing += (_, e) => { if (!_closed) { e.Cancel = true; window.Hide(); } }; _journalWindow = window; window.Show();
    }
    private void ShowPan()
    {
        var window = new Window { Owner = this }; MusicWishUi.PrepareWindow(window, T("Pan"), "VideoUpscale.Pan", 420); window.Height = 220;
        var panel = new StackPanel { Margin = new(16) }; panel.Children.Add(MusicWishUi.Text(T("PanX"))); panel.Children.Add(PanSlider(true));
        panel.Children.Add(MusicWishUi.Text(T("PanY"))); panel.Children.Add(PanSlider(false)); window.Content = panel; window.ShowDialog();
    }
}
