using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.MusicMidi;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Panel = System.Windows.Controls.Panel;
using ListBox = System.Windows.Controls.ListBox;
using MessageBoxButton = System.Windows.MessageBoxButton;

namespace AIHub.Controls;

public sealed class MusicMelodyWindow : Window
{
    private readonly MusicMelodyEditor _editor;
    private readonly MusicMelodyRoll _roll = new();
    private readonly Func<string,string> _l;
    private readonly CheckBox _enabled = new();
    private readonly TextBox _bpm = Input("Tempo"), _pitch = Input("Pitch"), _start = Input("Start"), _length = Input("Length");
    private readonly ComboBox _meter = new() { Width = 85 }, _snap = new() { Width = 85 };
    private readonly TextBlock _status = MusicAudioUi.Text(), _selection = MusicAudioUi.Text();
    private readonly System.Windows.Media.MediaPlayer _player = new();
    private readonly string _previewFolder = Path.Combine(Path.GetTempPath(), "LOPATA-Melody-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _closed = new();
    private readonly string _variation;
    private bool _previewing, _refreshing;
    private int _previewRevision;
    public MusicMelody Result { get; private set; }
    public MusicMelodyWindow(MusicMelody melody, string variation, Func<string,string> localize) {
        melody.Validate(); _editor = new(melody); Result = melody.Snapshot(); _variation = variation; _l = localize;
        MusicWishUi.PrepareWindow(this,L("Title"),"Music.Midi.Window",1080);
        Height = 800; MinWidth = 760; MinHeight = 620;
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush"); SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        AutomationProperties.SetAutomationId(this, "Music.Midi.Window");
        var root = new DockPanel { Margin = new(16) }; Content = root;
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        _status.TextWrapping = TextWrapping.Wrap; _status.Margin = new(0,8,0,8); footer.Children.Add(_status);
        var close = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        close.Children.Add(Button("Apply", () => { SyncTempo(); var result = _editor.Current with { Enabled = _enabled.IsChecked == true }; result.Validate(); Result = result.Snapshot(); DialogResult = true; }));
        close.Children.Add(Button("Cancel", () => DialogResult = false)); footer.Children.Add(close);
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var title = MusicAudioUi.Text(21); title.Text = L("Title"); title.FontWeight = FontWeights.SemiBold; header.Children.Add(title);
        var description = MusicAudioUi.Text(); description.Text = L(variation == MusicStudioRuntime.Variation ? "Studio" : variation == MusicAceCatalog.Variation ? "Ace" : "Unsupported");
        description.TextWrapping = TextWrapping.Wrap; description.Margin = new(0,8,0,8); header.Children.Add(description);
        _enabled.Content = L("Enabled"); _enabled.IsChecked = MusicMelodyPipeline.Supported(variation) && melody.Enabled; _enabled.IsEnabled = MusicMelodyPipeline.Supported(variation);
        AutomationProperties.SetAutomationId(_enabled, "Music.Midi.Enabled"); header.Children.Add(_enabled);
        var files = Row(); header.Children.Add(files);
        files.Children.Add(Button("Import", Import)); files.Children.Add(Button("OpenProject", OpenProject));
        files.Children.Add(Button("Export", Export)); files.Children.Add(Button("SaveProject", SaveProject));
        var settings = Row(); header.Children.Add(settings);
        Field(settings, "Tempo", _bpm); Field(settings, "Meter", _meter); Field(settings, "Snap", _snap);
        foreach (var value in new[] { "2/4", "3/4", "4/4", "5/4", "6/8", "7/8", "12/8" }) _meter.Items.Add(value);
        var meter = melody.Numerator + "/" + melody.Denominator;
        if (!_meter.Items.Contains(meter)) _meter.Items.Add(meter); _meter.SelectedItem = meter;
        foreach (var value in new[] { "1/4", "1/8", "1/16", "1/32" }) _snap.Items.Add(value); _snap.SelectedIndex = 1; _roll.Snap = 240;
        _snap.SelectionChanged += (_,_) => _roll.Snap = 480 / (1 << Math.Max(0,_snap.SelectedIndex));
        _bpm.LostKeyboardFocus += (_,_) => Attempt(SyncTempo);
        _meter.SelectionChanged += (_,_) => { if (!_refreshing) Attempt(SyncTempo); };
        settings.Children.Add(Button("Preview", async () => await PreviewAsync()));
        settings.Children.Add(Button("Stop", Stop));
        var zoom = new System.Windows.Controls.Slider { Width = 100, Minimum = 36, Maximum = 180, Value = 72, Margin = new(12,0,0,0) };
        zoom.ToolTip = L("Zoom"); zoom.ValueChanged += (_,_) => _roll.Zoom(zoom.Value); settings.Children.Add(zoom);
        var actions = Row(); header.Children.Add(actions);
        actions.Children.Add(Button("Undo", () => { _editor.Undo(); Refresh(); })); actions.Children.Add(Button("Redo", () => { _editor.Redo(); Refresh(); }));
        actions.Children.Add(Button("Delete", () => Edit(() => _editor.Remove(_roll.Selection))));
        actions.Children.Add(Button("Repeat", () => Edit(() => _editor.Repeat(_roll.Selection))));
        actions.Children.Add(Button("Down", () => Edit(() => _editor.Transpose(_roll.Selection,-1))));
        actions.Children.Add(Button("Up", () => Edit(() => _editor.Transpose(_roll.Selection,1))));
        actions.Children.Add(Button("All", () => { _roll.Selection.UnionWith(_editor.Current.Notes.Select(n => n.Id)); Selected(); _roll.InvalidateVisual(); }));
        var note = Row(); header.Children.Add(note); Field(note,"Pitch",_pitch); Field(note,"Start",_start); Field(note,"Length",_length);
        note.Children.Add(Button("SetNote", SetNote)); note.Children.Add(_selection);
        var hint = MusicAudioUi.Text(12); hint.Text = L("Hint"); hint.TextWrapping = TextWrapping.Wrap; hint.Margin = new(0,5,0,8); header.Children.Add(hint);
        var scroll = new ScrollViewer { Content = _roll, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        root.Children.Add(scroll); Loaded += (_,_) => scroll.ScrollToVerticalOffset((127-Math.Min(127, Math.Max(72, melody.Notes.Select(n=>n.Pitch).DefaultIfEmpty(72).Max())))*20);
        scroll.ScrollChanged += (_,_) => _roll.Viewport(scroll.VerticalOffset);
        _roll.Edited = next => Attempt(() => Edit(() => _editor.Set(next))); _roll.SelectionChanged = Selected;
        _roll.KeyDown += (_,e) => {
            if (e.Key == Key.Delete) { Attempt(() => Edit(() => _editor.Remove(_roll.Selection))); e.Handled = true; }
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.Z or Key.Y) {
                if (e.Key == Key.Z) _editor.Undo(); else _editor.Redo(); Refresh(); e.Handled = true;
            }
        };
        _player.MediaOpened += (_,_) => { if (!_closed.IsCancellationRequested) _player.Play(); };
        _player.MediaFailed += (_,_) => { _status.Text = L("PreviewFailed"); _player.Close(); };
        Closed += (_,_) => { _closed.Cancel(); _player.Close(); try { if (Directory.Exists(_previewFolder)) Directory.Delete(_previewFolder,true); } catch (IOException) { } _closed.Dispose(); };
        Refresh();
    }
    private string L(string key) => _l("Music.Midi." + key);
    private static TextBox Input(string name) { var box = new TextBox { Width = 76, MinHeight = 30, Margin = new(4,0,10,0) }; AutomationProperties.SetAutomationId(box,"Music.Midi."+name); return box; }
    private static WrapPanel Row() => new() { Margin = new(0,6,0,0), VerticalAlignment = VerticalAlignment.Center };
    private void Field(Panel row, string name, UIElement input) { var label = MusicAudioUi.Text(12); label.Text = L(name); label.VerticalAlignment = VerticalAlignment.Center; AutomationProperties.SetName(input,L(name)); row.Children.Add(label); row.Children.Add(input); }
    private Button Button(string key, Action action) {
        var button = new Button { Content = L(key), FontSize = 13, Margin = new(0,0,5,0), Padding = new(9,5,9,5), MinHeight = 30 };
        AutomationProperties.SetAutomationId(button,"Music.Midi."+key); button.Click += (_,_) => Attempt(action); return button;
    }
    private void Attempt(Action action) { try { action(); } catch (Exception e) { _status.Text = e.Message.StartsWith("Music.Midi.") ? _l(e.Message) : L("Failed") + " " + e.Message; } }
    private void Edit(Action action) { Stop(); try { action(); Refresh(); } catch { _roll.Set(_editor.Current); Selected(); throw; } }
    private void Refresh() {
        _refreshing = true; _bpm.Text = _editor.Current.Bpm.ToString(CultureInfo.InvariantCulture);
        var meter = _editor.Current.Numerator + "/" + _editor.Current.Denominator; if (!_meter.Items.Contains(meter)) _meter.Items.Add(meter); _meter.SelectedItem = meter;
        _roll.Set(_editor.Current); _refreshing = false; Selected();
        _status.Text = string.Format(L("Summary"), _editor.Current.Notes.Length, _editor.Current.Seconds.ToString("0.##"));
    }
    private void Selected() {
        var note = _editor.Current.Notes.SingleOrDefault(n => _roll.Selection.Count == 1 && _roll.Selection.Contains(n.Id));
        _selection.Text = string.Format(L("Selected"), _roll.Selection.Count);
        _pitch.Text = note?.Pitch.ToString() ?? "60"; _start.Text = (note?.Start / 480d ?? 0).ToString("0.###",CultureInfo.InvariantCulture);
        _length.Text = (note?.Length / 480d ?? 1).ToString("0.###",CultureInfo.InvariantCulture);
    }
    private void SyncTempo() {
        if (!int.TryParse(_bpm.Text,out var bpm)) throw new InvalidDataException("Music.Midi.TempoRange");
        var meter = ((string)_meter.SelectedItem).Split('/');
        var next = _editor.Current with { Bpm = bpm, Numerator = int.Parse(meter[0]), Denominator = int.Parse(meter[1]) };
        if (next.Bpm != _editor.Current.Bpm || next.Numerator != _editor.Current.Numerator || next.Denominator != _editor.Current.Denominator) Edit(() => _editor.Set(next));
    }
    private void SetNote() {
        if (!int.TryParse(_pitch.Text,out var pitch) || !double.TryParse(_start.Text.Replace(',','.'),NumberStyles.Float,CultureInfo.InvariantCulture,out var start)
            || !double.TryParse(_length.Text.Replace(',','.'),NumberStyles.Float,CultureInfo.InvariantCulture,out var length)
            || !double.IsFinite(start) || !double.IsFinite(length) || start < 0 || length <= 0 || start > 1e5 || length > 1e5) throw new InvalidDataException("Music.Midi.Invalid");
        var selected = _editor.Current.Notes.FirstOrDefault(n => _roll.Selection.Count == 1 && _roll.Selection.Contains(n.Id));
        var note = new MusicMelodyNote(pitch, (int)Math.Round(start*8)*60, Math.Max(60,(int)Math.Round(length*8)*60)) { Id = selected?.Id ?? Guid.NewGuid().ToString("N") };
        Edit(() => _editor.Set(_editor.Current with { Notes = _editor.Current.Notes.Where(n => n.Id != note.Id).Append(note).ToArray() }));
        _roll.Selection.Clear(); _roll.Selection.Add(note.Id); Selected(); _roll.InvalidateVisual();
    }
    private void Import() {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "MIDI|*.mid;*.midi", Multiselect = false }; if (dialog.ShowDialog(this) != true) return;
        var tracks = MusicMidiFile.Read(dialog.FileName); var chosen = tracks.Length == 1 ? tracks[0] : Choose(tracks); if (chosen is null) return;
        var warning = string.Join("\n\n", chosen.Melody.ImportWarnings.Select(L));
        if (System.Windows.MessageBox.Show(this,warning,L("Import"),MessageBoxButton.OKCancel,MessageBoxImage.Information) != MessageBoxResult.OK) return;
        Edit(() => _editor.Set(chosen.Melody with { Enabled = false }));
    }
    private MusicMidiTrack? Choose(MusicMidiTrack[] tracks) {
        var window = new Window { Owner = this, Title = L("ChooseTrack"), Width = 560, Height = 320, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        MusicWishUi.PrepareWindow(window,L("ChooseTrack"),"Music.Midi.ChooseTrack",560); window.Height = 320;
        window.SetResourceReference(BackgroundProperty,"WindowBackgroundBrush"); window.SetResourceReference(ForegroundProperty,"TextPrimaryBrush");
        var panel = new DockPanel { Margin = new(16) }; var button = new Button { Content = L("Apply"), Padding = new(12) }; DockPanel.SetDock(button,Dock.Bottom); panel.Children.Add(button);
        var list = new ListBox { ItemsSource = tracks.Select(t => t.Name + " · " + t.OriginalNotes + " → " + t.Melody.Notes.Length).ToArray(), SelectedIndex = 0 }; panel.Children.Add(list); window.Content = panel;
        button.Click += (_,_) => window.DialogResult = true;
        return window.ShowDialog() == true ? tracks[list.SelectedIndex] : null;
    }
    private void OpenProject() {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "LOPATA Melody|*.lopata-melody.json" }; if (dialog.ShowDialog(this) != true) return;
        Edit(() => _editor.Set(MusicMidiFile.ReadProject(dialog.FileName) with { Enabled = false }));
    }
    private void Export() {
        SyncTempo(); var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "MIDI|*.mid", FileName = "Melody.mid", AddExtension = true };
        if (dialog.ShowDialog(this) == true) { MusicMidiFile.Write(dialog.FileName,_editor.Current); _status.Text = L("Saved"); }
    }
    private void SaveProject() {
        SyncTempo(); var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "LOPATA Melody|*.lopata-melody.json", FileName = "Melody.lopata-melody.json" };
        if (dialog.ShowDialog(this) == true) { MusicMidiFile.SaveProject(dialog.FileName,_editor.Current with { Enabled = _enabled.IsChecked == true }); _status.Text = L("Saved"); }
    }
    private async Task PreviewAsync() {
        if (_previewing) return; _previewing = true;
        try {
            SyncTempo(); Stop(); Directory.CreateDirectory(_previewFolder); var path = Path.Combine(_previewFolder,Guid.NewGuid().ToString("N")+".wav");
            var melody = _editor.Current.Snapshot(); var token = _closed.Token; var revision = _previewRevision;
            await Task.Run(() => MusicMelodyWave.Write(path,melody,token),token);
            if (!token.IsCancellationRequested && revision == _previewRevision) { _player.Open(new Uri(path)); _status.Text = L("PreviewHint"); }
        } catch (OperationCanceledException) { }
        catch (Exception e) { if (!_closed.IsCancellationRequested) _status.Text = e.Message.StartsWith("Music.Midi.") ? _l(e.Message) : L("PreviewFailed"); }
        finally { _previewing = false; if (_closed.IsCancellationRequested) try { if (Directory.Exists(_previewFolder)) Directory.Delete(_previewFolder,true); } catch (IOException) { } }
    }
    private void Stop() { _previewRevision++; _player.Close(); }
}
