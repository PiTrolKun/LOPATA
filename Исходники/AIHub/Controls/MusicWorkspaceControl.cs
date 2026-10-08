using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AIHub.Services;
using UserControl = System.Windows.Controls.UserControl;

namespace AIHub.Controls;

public sealed class MusicWorkspaceControl : UserControl, IDisposable
{
    public MusicLyricsEditor Editor { get; } = new();
    public MusicWishesControl Wishes { get; } = new();
    public MusicTextActionsControl TextActions { get; }
    public MusicPlayerControl Player { get; } = new();
    public MusicTracksControl Tracks { get; } = new();
    public MusicStatusControl Status { get; } = new();
    public MusicGenerationControl Generation { get; }
    public MusicGenerationSession Session { get; }
    public MusicProjectController Projects { get; }
    private bool _outputConfigured;
    public MusicWorkspaceControl(MusicProjects? projects = null, MusicGenerationJobs? jobs = null, MusicOutputPreferences? outputPreferences = null)
    {
        Generation = new(outputPreferences);
        AutomationProperties.SetAutomationId(this, "Music.Workspace");
        var grid = new Grid { Margin = new(8) };
        var editorWidth = new ColumnDefinition { Width = new(360), MinWidth = 250 };
        grid.ColumnDefinitions.Add(editorWidth);
        grid.ColumnDefinitions.Add(new() { Width = new(12) });
        grid.ColumnDefinitions.Add(new() { Width = new(140) });
        grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star), MinWidth = 220 });
        grid.ColumnDefinitions.Add(new() { Width = new(1.4, GridUnitType.Star), MinWidth = 260 });
        grid.RowDefinitions.Add(new() { Height = new(110) });
        grid.RowDefinitions.Add(new() { Height = new(190) });
        grid.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        Add(Card(Editor), 0, 0, 3);
        var divider = new Thumb { Cursor = System.Windows.Input.Cursors.SizeWE, Width = 10,
            VerticalAlignment = VerticalAlignment.Stretch, Opacity = .6 };
        divider.SetResourceReference(BackgroundProperty, "LineBrush");
        AutomationProperties.SetAutomationId(divider, "Music.EditorDivider");
        divider.DragDelta += (_, e) => editorWidth.Width = new(Math.Clamp(editorWidth.ActualWidth + e.HorizontalChange, 250, MaximumWidth()));
        grid.SizeChanged += (_, _) =>
        { if (grid.ActualWidth > 0) editorWidth.Width = new(Math.Clamp(editorWidth.Width.Value, 250, MaximumWidth())); };
        Add(divider, 1, 0, 3);
        Add(Card(Wishes), 2, 0, 3);
        Add(Card(Status), 3, 0, 1, 2);
        Add(Card(Player), 3, 1);
        var tracks = new Grid(); tracks.ColumnDefinitions.Add(new() { Width = new(54) }); tracks.ColumnDefinitions.Add(new());
        TextActions = new MusicTextActionsControl(Editor, () => Wishes.State.Performers);
        Projects = new(this, projects ?? MusicProjects.Default, jobs ?? MusicGenerationJobs.Default);
        Editor.AttachHistory(Projects.History);
        var tools = new DockPanel(); DockPanel.SetDock(Projects.ManageButton, Dock.Bottom);
        tools.Children.Add(Projects.ManageButton); tools.Children.Add(TextActions);
        var textActions = Card(tools); tracks.Children.Add(textActions);
        var results = Card(Tracks); Grid.SetColumn(results, 1); tracks.Children.Add(results);
        Tracks.Selected += (track, play) => Player.Select(track, play);
        Tracks.FolderChanged += Player.ConfigureFolder;
        Player.PlaybackChanged += () => Tracks.SetPlaying(Player.IsPlaying);
        Add(tracks, 3, 2);
        Add(Card(Generation), 4, 1, 2);
        Content = grid;
        Session = new(this, jobs);
        double MaximumWidth() => Math.Max(250, grid.ActualWidth - 140 - 12 - 220 - 260);
        void Add(UIElement element, int column, int row, int rowSpan = 1, int columnSpan = 1)
        { Grid.SetColumn(element, column); Grid.SetRow(element, row); Grid.SetRowSpan(element, rowSpan); Grid.SetColumnSpan(element, columnSpan); grid.Children.Add(element); }
    }
    public void Localize(Func<string, string> localize)
    {
        Editor.Localize(localize);
        Wishes.Localize(localize);
        TextActions.Localize(localize);
        Player.Localize(localize);
        Tracks.Localize(localize);
        Status.Localize(localize);
        Generation.Localize(localize);
        Projects.Localize(localize);
        var grid = (Grid)Content;
        var divider = (Thumb)grid.Children[1]; divider.ToolTip = localize("Music.Editor.Resize");
        AutomationProperties.SetName(divider, localize("Music.Editor.Resize"));
    }
    private static Border Card(UIElement child)
    {
        var border = new Border { Child = child, CornerRadius = new(12), BorderThickness = new(1), Margin = new(4) };
        border.SetResourceReference(BackgroundProperty, "PanelBrush"); border.SetResourceReference(Border.BorderBrushProperty, "LineBrush"); return border;
    }
    public void ConfigureOutput(string folder, Action<string> save)
    { if (_outputConfigured) return; _outputConfigured = true;
        Tracks.ConfigureFolder(folder, save); Player.ConfigureFolder(folder); Projects.RememberDefaults(); }
    public void ConfigureGeneration(string modelsRoot, Func<string, string> localize) => Session.Configure(modelsRoot, localize);
    public void Dispose() { Session.Dispose(); Editor.Dispose(); Player.Dispose(); Status.Dispose(); }
}
