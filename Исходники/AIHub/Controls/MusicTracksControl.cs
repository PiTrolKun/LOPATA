using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AIHub.Models;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using ListBox = System.Windows.Controls.ListBox;
using UserControl = System.Windows.Controls.UserControl;
using Brushes = System.Windows.Media.Brushes;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace AIHub.Controls;

public sealed class MusicTracksControl : UserControl
{
    private readonly List<MusicTrack> _tracks = [];
    private readonly ListBox _list = new() { BorderThickness = new(0), Padding = new(0), Background = Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _folder = MusicAudioUi.Text(11), _heading = MusicAudioUi.Text(14), _message = MusicAudioUi.Text(11);
    private readonly Button _choose, _open;
    private Func<string, string> _l = key => key;
    private Action<string>? _saveFolder;
    private bool _playing, _rebuilding;
    public string OutputFolder { get; private set; } = "";
    public MusicTrack? SelectedTrack => (_list.SelectedItem as ListBoxItem)?.Tag as MusicTrack;
    public bool HasTrack(string path) => _tracks.Any(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase));
    public event Action<MusicTrack, bool>? Selected;
    public event Action<string>? FolderChanged;
    public MusicTracksControl()
    {
        AutomationProperties.SetAutomationId(this, "Music.Audio.Tracks");
        var root = new DockPanel { Margin = new(10) };
        var header = new StackPanel(); _heading.FontWeight = FontWeights.SemiBold; header.Children.Add(_heading);
        var toolbar = new Grid { Margin = new(0, 3, 0, 4) };
        toolbar.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); toolbar.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); toolbar.ColumnDefinitions.Add(new());
        _choose = MusicAudioUi.IconButton("ChooseFolder", "M2,7 V21 H22 V6 H11 L8,3 H2 V7 M2,9 H22", ChooseFolder);
        _open = MusicAudioUi.IconButton("OpenFolder", "M2,7 V21 H19 L23,10 H7 L2,21 M2,7 V3 H9 L12,6 H20 V10", OpenFolder);
        toolbar.Children.Add(_choose); Grid.SetColumn(_open, 1); toolbar.Children.Add(_open); Grid.SetColumn(_folder, 2); toolbar.Children.Add(_folder);
        _folder.SizeChanged += (_, _) => UpdateFolder(); header.Children.Add(toolbar);
        _message.TextWrapping = TextWrapping.Wrap; _message.Visibility = Visibility.Collapsed; header.Children.Add(_message);
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(_list, ScrollBarVisibility.Auto);
        _list.SelectionChanged += (_, _) => { if (_rebuilding) return; UpdateSelection(); if (SelectedTrack is { } track) Selected?.Invoke(track, false); };
        root.Children.Add(_list); Content = root;
        Rebuild();
    }
    public void ConfigureFolder(string folder, Action<string> save)
    { OutputFolder = folder ?? ""; _saveFolder = save; UpdateFolder(); }
    public void Localize(Func<string, string> localize)
    {
        _l = localize; _heading.Text = _l("Music.Audio.Tracks");
        MusicAudioUi.Label(_choose, _l("Music.Audio.ChooseFolder")); MusicAudioUi.Label(_open, _l("Music.Audio.OpenFolder"));
        UpdateFolder(); Rebuild();
    }
    public void AddTrack(MusicTrack track)
    {
        if (track.IsExample || !File.Exists(track.Path)) throw new ArgumentException("A completed audio file is required.", nameof(track));
        if (_tracks.Any(t => string.Equals(t.Path, track.Path, StringComparison.OrdinalIgnoreCase))) return;
        _tracks.Insert(0, track); Rebuild(track);
        Selected?.Invoke(track, false);
    }
    public void SetPlaying(bool playing) { _playing = playing; UpdateSelection(); }
    public void ClearTracks() { _tracks.Clear(); _playing = false; _message.Visibility = Visibility.Collapsed; Rebuild(); }
    public void SetOutputFolder(string folder) { OutputFolder = folder; UpdateFolder(); FolderChanged?.Invoke(folder); }
    private void Rebuild(MusicTrack? select = null)
    {
        select ??= SelectedTrack ?? _tracks.FirstOrDefault();
        _rebuilding = true; _list.Items.Clear();
        foreach (var track in _tracks)
        {
            var card = new Border { CornerRadius = new(8), BorderThickness = new(1), Padding = new(8), Margin = new(0, 3, 0, 3) };
            card.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
            var row = new Grid(); row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var labels = new StackPanel(); var title = MusicAudioUi.Text(13); title.FontWeight = FontWeights.SemiBold;
            title.Text = track.IsExample ? _l(track.Title) : track.Title; title.ToolTip = title.Text; labels.Children.Add(title);
            var detail = MusicAudioUi.Text(11); detail.Margin = new(0, 4, 0, 0);
            detail.Text = MusicAudioUi.Time(track.Duration) + " · " + track.CreatedAt.ToString("dd.MM.yyyy HH:mm"); labels.Children.Add(detail);
            if (track.AdditionalPath is { } extra) {
                var formats = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
                foreach (var path in new[] { track.Path!, extra }) {
                    var format = new Button { Content = Path.GetExtension(path).TrimStart('.').ToUpperInvariant(), Padding = new(4, 1, 4, 1),
                        MinWidth = 0, MinHeight = 0, FontSize = 10, Margin = new(0, 3, 4, 0), ToolTip = path };
                    AutomationProperties.SetAutomationId(format, "Music.Audio.Format." + Path.GetExtension(path).TrimStart('.'));
                    format.IsEnabled = File.Exists(path);
                    format.Click += (_, e) => { _list.SelectedItem = _list.Items.OfType<ListBoxItem>().Single(item => Equals(item.Tag, track));
                        Selected?.Invoke(track with { Path = path, Title = Path.GetFileName(path) }, false); e.Handled = true; };
                    formats.Children.Add(format);
                }
                labels.Children.Add(formats);
            }
            if (track.IsExample) { var example = MusicAudioUi.Text(11); example.Text = _l("Music.Audio.Example"); labels.Children.Add(example); }
            row.Children.Add(labels);
            var copy = MusicAudioUi.IconButton("Copy", "M8,7 H21 V22 H8 Z M16,7 V2 H3 V17 H8", () => Copy(track));
            copy.IsEnabled = !track.IsExample && File.Exists(track.Path); MusicAudioUi.Label(copy, _l("Music.Audio.Copy"));
            Grid.SetColumn(copy, 1); row.Children.Add(copy); card.Child = row;
            var item = new ListBoxItem { Tag = track, Content = card, Padding = new(0), MinWidth = 0, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            item.Template = new ControlTemplate(typeof(ListBoxItem)) { VisualTree = presenter };
            item.MouseDoubleClick += (_, e) =>
            { if (e.OriginalSource is DependencyObject source && HasButtonAncestor(source)) return; _list.SelectedItem = item; Selected?.Invoke(track, true); e.Handled = true; };
            _list.Items.Add(item); if (track == select) _list.SelectedItem = item;
        }
        _rebuilding = false; UpdateSelection();
    }
    private void UpdateSelection()
    {
        foreach (var item in _list.Items.OfType<ListBoxItem>())
        {
            var card = (Border)item.Content; card.SetResourceReference(Border.BorderBrushProperty, item.IsSelected ? "AccentBrush" : "LineBrush");
            var title = (TextBlock)((StackPanel)((Grid)card.Child).Children[0]).Children[0]; var track = (MusicTrack)item.Tag;
            title.Text = (item.IsSelected && _playing ? "▶ " : "") + (track.IsExample ? _l(track.Title) : track.Title);
        }
    }
    private void UpdateFolder()
    { MusicAudioUi.PathText(_folder, string.IsNullOrEmpty(OutputFolder) ? _l("Music.Audio.NoFolder") : OutputFolder); _open.IsEnabled = Directory.Exists(OutputFolder); }
    private void ChooseFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = _l("Music.Audio.ChooseFolder"), InitialDirectory = Directory.Exists(OutputFolder) ? OutputFolder : "" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try { _saveFolder?.Invoke(dialog.FolderName); OutputFolder = dialog.FolderName; UpdateFolder(); FolderChanged?.Invoke(OutputFolder); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Error(e); }
    }
    private void OpenFolder()
    {
        if (!Directory.Exists(OutputFolder)) return;
        try { Process.Start(new ProcessStartInfo(OutputFolder) { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException) { Error(e); }
    }
    private void Copy(MusicTrack track)
    {
        if (track.IsExample || !File.Exists(track.Path)) return;
        try {
            var files = new System.Collections.Specialized.StringCollection { Path.GetFullPath(track.Path!) };
            if (track.AdditionalPath is { } extra && File.Exists(extra)) files.Add(Path.GetFullPath(extra));
            Clipboard.SetFileDropList(files); _message.Text = _l("Music.Audio.Copied"); _message.Visibility = Visibility.Visible;
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.ExternalException or IOException) { Error(e); }
    }
    private void Error(Exception e) { _message.Text = _l("Music.Audio.FileError") + " " + e.Message; _message.Visibility = Visibility.Visible; }
    private static bool HasButtonAncestor(DependencyObject source)
    { for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node)) if (node is Button) return true; return false; }
}
