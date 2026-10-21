using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using TabControl = System.Windows.Controls.TabControl;
using Orientation = System.Windows.Controls.Orientation;

namespace AIHub.Controls;

public sealed partial class MusicAudioTaskWindow : Window
{
    private readonly Func<string, string> _l;
    private readonly string _variation;
    private readonly MusicAudioTask _initial;
    private readonly CheckBox _enabled = new(), _harmony = new(), _noise = new(), _normalize = new();
    private readonly TextBox _source = new() { IsReadOnly = true }, _start = new() { Text = "0", Width = 90 }, _end = new() { Width = 90 };
    private readonly TextBox _lyrics = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 120, MaxHeight = 250, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox _originalLyrics = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 80, MaxHeight = 200, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox _editStart = new() { Width = 90 }, _editEnd = new() { Width = 90 };
    private readonly ComboBox _coverMode = new(), _editMode = new();
    private readonly Slider _strength = new() { Minimum = 0, Maximum = 1, TickFrequency = .05, IsSnapToTickEnabled = true, MinWidth = 200 };
    private readonly StackPanel _references = new();
    private readonly StackPanel _referenceControls = new();
    private readonly TextBlock _sourceRole = MusicWishUi.Text(""), _referenceRole = MusicWishUi.Text("");
    private readonly List<ReferenceRow> _rows = [];
    private readonly TabControl _tabs = new();
    private readonly TextBlock _status = MusicWishUi.Text("");
    private readonly TextBlock _inputUsage = MusicWishUi.Text("");
    private readonly string? _current;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _readingLyrics, _closing;
    public MusicAudioTask Result { get; private set; }
    public string Lyrics => _lyrics.Text;
    public MusicAudioTaskWindow(Window? owner, string variation, MusicAudioTask task, string lyrics, string? current, Func<string, string> l, Func<string, AIHub.Services.MusicLibrary.LyricEntry>? libraryDraft = null)
    {
        _l = l; _variation = variation; _initial = task.Snapshot(); _current = current; Result = task.Snapshot();
        MusicWishUi.PrepareWindow(this, T("Title"), "Music.Transform.Window", 960); Height = 850; MinWidth = 640; MinHeight = 520;
        if (owner is not null) Owner = owner;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/MusicAudioTaskResources.xaml", UriKind.Relative) });
        var root = new DockPanel { Margin = new(20) };
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        bottom.Children.Add(_status); var actions = new WrapPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new(0, 12, 0, 0) };
        actions.Children.Add(MusicWishUi.Button(T("Apply"), "Music.Transform.Apply", Apply));
        actions.Children.Add(MusicWishUi.Button(_l("Music.Lora.Cancel"), "Music.Transform.Close", Close)); bottom.Children.Add(actions);
        var body = new StackPanel(); root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var heading = MusicWishUi.Text(T("Title") + " · " + MusicModelVariants.Name(variation)); heading.FontSize = 22; body.Children.Add(heading);
        body.Children.Add(MusicWishUi.Text(T(variation == MusicStudioRuntime.Variation ? "StudioHint" : variation == MusicAceCatalog.Variation ? "AceHint" : "GenericHint")));
        _enabled.Content = T("Enabled"); _enabled.IsChecked = task.Enabled; body.Children.Add(_enabled); Id(_enabled, "Enabled");
        _inputUsage.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush"); body.Children.Add(_inputUsage); Id(_inputUsage, "InputUsage");
        body.Children.Add(Label("Source")); body.Children.Add(_sourceRole); Id(_sourceRole, "SourceRole"); body.Children.Add(_source); Id(_source, "Source");
        var sourceActions = new WrapPanel(); sourceActions.Children.Add(MusicWishUi.Button(T("Browse"), "Music.Transform.Browse", PickSource));
        var currentButton = MusicWishUi.Button(T("CurrentTrack"), "Music.Transform.Current", () => SetSource(_current!)); currentButton.IsEnabled = !string.IsNullOrWhiteSpace(_current); sourceActions.Children.Add(currentButton);
        sourceActions.Children.Add(MusicWishUi.Button(T("Listen"), "Music.Transform.ListenSource", () => Preview(_source.Text))); sourceActions.Children.Add(MusicWishUi.Button(T("EmbeddedLyrics"), "Music.Transform.EmbeddedLyrics", () => _ = LoadLyricsAsync(false)));
        sourceActions.Children.Add(MusicWishUi.Button(T("RecognizeLyrics"), "Music.Transform.RecognizeLyrics", () => _ = LoadLyricsAsync(true)));
        body.Children.Add(sourceActions);
        body.Children.Add(Range("SourceRange", _start, _end));
        if (task.Source is { } input) { SetSource(input.Path); _start.Text = Number(input.Start); _end.Text = input.End is { } finish ? Number(finish) : ""; }
        AddOriginalLyrics(body, task.SourceLyrics, libraryDraft);
        var cover = new StackPanel(); _coverMode.Items.Add(Choice("Mode.Cover", MusicAudioTaskMode.Cover));
        _coverMode.Items.Add(Choice("Mode.ReplaceVoice", MusicAudioTaskMode.ReplaceVoice)); _coverMode.Items.Add(Choice("Mode.ReplaceMusic", MusicAudioTaskMode.ReplaceMusic));
        _coverMode.SelectedIndex = task.Mode == MusicAudioTaskMode.ReplaceVoice ? 1 : task.Mode == MusicAudioTaskMode.ReplaceMusic ? 2 : 0; cover.Children.Add(_coverMode); Id(_coverMode, "Mode");
        _harmony.Content = T("KeepHarmony"); _harmony.IsChecked = task.KeepHarmony; _harmony.Visibility = variation == MusicStudioRuntime.Variation ? Visibility.Visible : Visibility.Collapsed; cover.Children.Add(_harmony);
        var strength = new Grid(); strength.ColumnDefinitions.Add(new()); strength.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _strength.Value = task.SourceStrength; var value = MusicWishUi.Text(Number(task.SourceStrength)); Grid.SetColumn(value, 1); strength.Children.Add(_strength); strength.Children.Add(value);
        _strength.ValueChanged += (_, _) => value.Text = _strength.Value.ToString("0.00", CultureInfo.CurrentCulture); Id(_strength, "Strength");
        if (variation == MusicAceCatalog.Variation) { cover.Children.Add(Label("SourceStrength")); cover.Children.Add(strength); } cover.Children.Add(MusicWishUi.Text(T("LayerHint")));
        _tabs.Items.Add(new TabItem { Header = T("TabCover"), Content = cover });
        _tabs.Items.Add(new TabItem { Header = T("TabReferences"), Content = MusicWishUi.Text(T("ReferencesOnlyHint")) });
        var edit = new StackPanel(); if (variation == MusicAceCatalog.Variation) _editMode.Items.Add(Choice("Mode.Repaint", MusicAudioTaskMode.Repaint));
        _editMode.Items.Add(Choice("Mode.Restore", MusicAudioTaskMode.Restore)); _editMode.SelectedIndex = task.Mode == MusicAudioTaskMode.Restore ? _editMode.Items.Count - 1 : 0;
        edit.Children.Add(_editMode); Id(_editMode, "EditMode"); _editStart.Text = Number(task.EditStart); _editEnd.Text = Number(task.EditEnd);
        edit.Children.Add(Range("EditRange", _editStart, _editEnd));
        _noise.Content = T("ReduceNoise"); _noise.IsChecked = task.ReduceNoise; edit.Children.Add(_noise);
        _normalize.Content = T("Normalize"); _normalize.IsChecked = task.Normalize; edit.Children.Add(_normalize); edit.Children.Add(MusicWishUi.Text(T("RestoreHint")));
        _tabs.Items.Add(new TabItem { Header = T("TabEdit"), Content = edit });
        _tabs.SelectedIndex = task.Mode == MusicAudioTaskMode.NewSong ? 1 : task.Mode is MusicAudioTaskMode.Repaint or MusicAudioTaskMode.Restore ? 2 : 0;
        var generatorSupported = variation == MusicAceCatalog.Variation || variation == MusicStudioRuntime.Variation;
        if (!generatorSupported) { ((TabItem)_tabs.Items[0]).IsEnabled = false; ((TabItem)_tabs.Items[1]).IsEnabled = false; _tabs.SelectedIndex = 2; }
        body.Children.Add(_tabs);
        body.Children.Add(Label("References")); body.Children.Add(_referenceRole); Id(_referenceRole, "ReferenceRole");
        body.Children.Add(MusicWishUi.Text(T("ReferenceHint")));
        var addReferences = MusicWishUi.Button(T("AddReferences"), "Music.Transform.AddReferences", AddReferences);
        _referenceControls.Children.Add(addReferences); _referenceControls.Children.Add(_references); body.Children.Add(_referenceControls); Id(_referenceControls, "ReferenceControls");
        foreach (var item in task.References) AddReference(item);
        body.Children.Add(Label("TargetLyrics")); _lyrics.Text = lyrics; body.Children.Add(_lyrics); Id(_lyrics, "Lyrics");
        var offer = new LyricLibraryOffer { Draft = () => libraryDraft?.Invoke(_lyrics.Text) ?? new AIHub.Services.MusicLibrary.LyricEntry { Title = string.IsNullOrWhiteSpace(_source.Text) ? _l("Music.Library.Untitled") : System.IO.Path.GetFileNameWithoutExtension(_source.Text), Text = _lyrics.Text, Origin = "cover editor; source metadata / transcription reviewed by user; model history unknown" } };
        offer.Localize(l); _lyrics.TextChanged += (_, _) => offer.SetText(_lyrics.Text); offer.SetText(_lyrics.Text); body.Children.Add(offer);
        var advanced = new StackPanel();
        advanced.Children.Add(MusicWishUi.Text(T("AdvancedHint"))); body.Children.Add(new Expander { Header = T("Advanced"), Content = advanced });
        Content = root;
        _enabled.Checked += (_, _) => RefreshInputUsage(); _enabled.Unchecked += (_, _) => RefreshInputUsage();
        _coverMode.SelectionChanged += (_, _) => RefreshInputUsage(); _editMode.SelectionChanged += (_, _) => RefreshInputUsage();
        _tabs.SelectionChanged += (_, _) => RefreshInputUsage(); RefreshInputUsage();
        Closing += (_, e) => { if (_readingLyrics) { e.Cancel = true; _closing = true; _lifetime.Cancel(); } };
        Closed += (_, _) => { _lifetime.Cancel(); _lifetime.Dispose(); };
    }
    private void Apply()
    {
        try {
            if (_readingLyrics) return;
            var mode = SelectedMode();
            var source = string.IsNullOrWhiteSpace(_source.Text) ? null : new MusicAudioInput(_source.Text) { Start = Read(_start), End = ReadOptional(_end) };
            var result = _initial with { Enabled = _enabled.IsChecked == true, Mode = mode, Source = source,
                References = _rows.Select(r => r.Read()).ToArray(), KeepHarmony = _harmony.IsChecked == true, SourceStrength = _strength.Value,
                EditStart = Read(_editStart), EditEnd = Read(_editEnd), ReduceNoise = _noise.IsChecked == true, Normalize = _normalize.IsChecked == true, SourceLyrics = _originalLyrics.Text };
            if (!string.IsNullOrEmpty(result.SourceLyrics)) MusicSourceText.Validate(result.SourceLyrics);
            result.Validate(_variation);
            if (result.Enabled && result.NeedsSeparation && (string.IsNullOrWhiteSpace(_lyrics.Text) || string.IsNullOrWhiteSpace(_originalLyrics.Text))) throw new InvalidDataException("Music.Transform.AlignmentLyrics");
            Result = result; DialogResult = true;
        } catch (Exception e) when (e is FormatException or InvalidDataException or ArgumentException) { _status.Text = e.Message.StartsWith("Music.") ? _l(e.Message) : T("InvalidInput"); }
    }
    private string T(string key) => _l("Music.Transform." + key);
    private TextBlock Label(string key) { var text = MusicWishUi.Text(T(key)); text.Margin = new(0, 12, 0, 4); return text; }
    private ComboBoxItem Choice(string key, MusicAudioTaskMode mode) => new() { Content = T(key), Tag = mode };
    private static string Number(double number) => number.ToString("0.###", CultureInfo.InvariantCulture);
    private static double Read(TextBox box) => double.Parse(box.Text.Replace(',', '.'), CultureInfo.InvariantCulture);
    private static double? ReadOptional(TextBox box) => string.IsNullOrWhiteSpace(box.Text) ? null : Read(box);
    private static void Id(DependencyObject control, string id) => AutomationProperties.SetAutomationId(control, "Music.Transform." + id);
    private FrameworkElement Range(string key, TextBox start, TextBox end)
    { var panel = new WrapPanel(); panel.Children.Add(Label(key)); panel.Children.Add(start); panel.Children.Add(MusicWishUi.Text(" — ")); panel.Children.Add(end); return panel; }
}
