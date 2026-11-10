using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Services;
using AIHub.Services.SpeechAssets;
using AIHub.Services.VideoEditor;
using Microsoft.Win32;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;
using Orientation = System.Windows.Controls.Orientation;
using ListBox = System.Windows.Controls.ListBox;

namespace AIHub.Controls.SpeechAssets;

internal sealed partial class SpeechAssetsControl : UserControl
{
    private readonly Window _owner;
    private readonly Func<string, string> _localize;
    private SpeechAssetStore _store;
    private SpeechAssetSession _session = new();
    private readonly TextBlock _source = new(), _voice = new(), _counts = new(), _status = new();
    private readonly ListBox _rows = new() { HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch };
    private readonly List<Button> _commands = [];
    private readonly MediaPlayer _player = new();
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _playTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private Slider? _playingSlider;
    private CancellationTokenSource? _cancel;
    private bool _busy, _seeking;
    internal string SessionId => _session.Id;
    internal string T(string key) => _localize("SpeechAssets." + key);
    internal SpeechAssetsControl(Window owner, Func<string, string> localize, SpeechAssetStore? store = null,
        SpeechAssetModelSession? resident = null, Func<VideoModelNeed, CancellationToken, Task<bool>>? prepareResident = null)
    {
        _owner = owner; _localize = localize; _store = store ?? new();
        _resident = resident ?? new(); _prepareResident = prepareResident;
        InitializeUpdates();
        IsVisibleChanged += ModelVisibilityChanged;
        _owner.Closed += async (_, _) => await LeaveScenarioAsync();
        Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("/AIHub;component/Controls/VideoEditor/VideoEditorResources.xaml", UriKind.Relative) });
        Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("/AIHub;component/Controls/SpeechAssets/SpeechAssetsResources.xaml", UriKind.Relative) });
        AllowDrop = true;
        DragOver += (_, e) => { e.Effects = !_busy && e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)
            ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None; e.Handled = true; };
        Drop += (_, e) =>
        {
            if (_busy || e.Data.GetData(System.Windows.DataFormats.FileDrop) is not string[] { Length: 1 } paths) return;
            try { if (Path.GetExtension(paths[0]).Equals(".md", StringComparison.OrdinalIgnoreCase)) ImportFile(paths[0]); }
            catch (Exception error) { Report(error); }
            e.Handled = true;
        };
        AutomationProperties.SetAutomationId(this, "SpeechAssets.View");
        SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        SetResourceReference(FontSizeProperty, "UiBodyFontSize");
        foreach (var label in new[] { _source, _voice, _counts, _status }) label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        _rows.SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        _rows.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        _rows.SetResourceReference(BorderBrushProperty, "LineBrush");
        var itemStyle = new Style(typeof(ListBoxItem));
        itemStyle.Setters.Add(new Setter(HorizontalContentAlignmentProperty, System.Windows.HorizontalAlignment.Stretch));
        itemStyle.Setters.Add(new Setter(BackgroundProperty, System.Windows.Media.Brushes.Transparent));
        itemStyle.Setters.Add(new Setter(ForegroundProperty, new DynamicResourceExtension("TextPrimaryBrush")));
        _rows.ItemContainerStyle = itemStyle;
        var root = new DockPanel { Margin = new(18) }; Content = root;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        var input = new DockPanel(); top.Children.Add(input);
        input.Children.Add(Command("Import", () => Import(), "Import"));
        input.Children.Add(Command("Examples", OpenExamples, "Examples", false));
        _source.Margin = new(12); _source.TextTrimming = TextTrimming.CharacterEllipsis; input.Children.Add(_source);
        var model = new DockPanel(); top.Children.Add(model);
        model.Children.Add(Command("Model", () => ChooseModel(), "Model"));
        _voice.Margin = new(12); _voice.TextWrapping = TextWrapping.Wrap; model.Children.Add(_voice);
        var actions = new WrapPanel(); top.Children.Add(actions);
        actions.Children.Add(Command("Run", () => _ = GenerateAsync(null), "Run"));
        actions.Children.Add(Command("Pause", () => _ = PauseAsync(), "Pause", false));
        actions.Children.Add(Command("Resume", () => _ = ResumePendingAsync(), "Resume", false));
        actions.Children.Add(Command("Stop", () => _cancel?.Cancel(), "Stop", false));
        actions.Children.Add(Command("Projects", ShowProjects, "Projects", false));
        actions.Children.Add(Command("Export", () => _ = ExportAsync(), "Export"));
        _counts.Margin = new(12); actions.Children.Add(_counts);
        AddExportSettings(top);
        _projectLabel.Margin = new(5); _projectLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        _projectLabel.TextTrimming = TextTrimming.CharacterEllipsis; top.Children.Add(_projectLabel);
        _status.Margin = new(5); _status.TextWrapping = TextWrapping.Wrap; top.Children.Add(_status);
        var template = new DataTemplate(); var factory = new FrameworkElementFactory(typeof(SpeechAssetRowView));
        factory.SetBinding(TagProperty, new System.Windows.Data.Binding { RelativeSource = new(System.Windows.Data.RelativeSourceMode.FindAncestor,
            typeof(SpeechAssetsControl), 1) }); template.VisualTree = factory; _rows.ItemTemplate = template;
        VirtualizingPanel.SetIsVirtualizing(_rows, true); VirtualizingPanel.SetVirtualizationMode(_rows, VirtualizationMode.Recycling);
        ScrollViewer.SetHorizontalScrollBarVisibility(_rows, ScrollBarVisibility.Disabled); root.Children.Add(_rows);
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); TrySave(); };
        _playTimer.Tick += (_, _) => { if (_playingSlider is not null && !_seeking) _playingSlider.Value = _player.Position.TotalSeconds; };
        _player.MediaEnded += (_, _) => StopAudio();
        _player.MediaFailed += (_, _) => { StopAudio(); _status.Text = T("InvalidAudio"); };
        Unloaded += (_, _) => { StopAudio(); _saveTimer.Stop(); TrySave(); };
        Loaded += (_, _) => { _owner.PreviewKeyDown -= ProjectKeyDown; _owner.PreviewKeyDown += ProjectKeyDown; };
        Unloaded += (_, _) => _owner.PreviewKeyDown -= ProjectKeyDown;
        var last = Path.Combine(_store.Root, "last.txt");
        try { if (File.Exists(last)) _session = _store.Load(File.ReadAllText(last).Trim()); }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or ArgumentException) { _status.Text = Error(e); }
        if (_session.Number == 0) _session.Number = _store.CreateDraft().Number;
        Refresh();
    }
    private Button Command(string key, Action action, string id, bool disableWhenBusy = true)
    {
        var button = new Button { Content = T(key), Margin = new(4), Padding = new(12, 7, 12, 7) };
        button.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        button.SetResourceReference(BackgroundProperty, "SecondaryButtonBackgroundBrush");
        button.SetResourceReference(BorderBrushProperty, "LineBrush");
        AutomationProperties.SetAutomationId(button, "SpeechAssets." + id);
        button.Click += (_, _) => { try { action(); } catch (Exception e) { _status.Text = Error(e); } };
        if (disableWhenBusy) _commands.Add(button); return button;
    }
    internal void Changed() { _saveTimer.Stop(); _saveTimer.Start(); Counts(); }
    internal void MoveRow(SpeechAssetRow row, int offset)
    {
        if (_busy) return;
        var index = _session.Rows.IndexOf(row); var target = index + offset;
        if (index < 0 || target < 0 || target >= _session.Rows.Count) return;
        StopAudio();
        _session.Rows.RemoveAt(index); _session.Rows.Insert(target, row);
        Changed(); Refresh(); _rows.ScrollIntoView(row);
    }
    private bool TrySave()
    {
        if (_busy) return true; // The worker owns persistence during synthesis; never save a stale UI snapshot.
        try
        {
            _store.Save(_session);
            RememberProject();
            return true;
        }
        catch (Exception e) { _status.Text = Error(e); return false; }
    }
    private string Error(Exception error) => error.Message.StartsWith("SpeechAssets.", StringComparison.Ordinal)
        || error.Message.StartsWith("VideoEditor.", StringComparison.Ordinal) || error.Message.StartsWith("ChatSpeech.", StringComparison.Ordinal)
        ? _localize(error.Message) : error.Message;
    internal void Report(Exception error) => _status.Text = Error(error);
    private void Import()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Markdown (*.md)|*.md" };
        if (dialog.ShowDialog(_owner) != true) return;
        ImportFile(dialog.FileName);
    }
    private void ImportFile(string path)
    {
        var text = File.ReadAllText(path, new System.Text.UTF8Encoding(false, true));
        var rows = SpeechAssetMarkdown.Parse(text);
        if (rows.Count == 0) throw new InvalidDataException("SpeechAssets.NoText");
        if (!TrySave()) return; StopAudio();
        _session = _store.CreateDraft(_session);
        _session.SourceName = Path.GetFileName(path); _session.SourcePath = path; _session.Rows = rows;
        TrySave(); _status.Text = T("Imported"); Refresh();
    }
    internal void OpenSavedSession(string id)
    {
        if (_busy) return;
        _saveTimer.Stop();
        if (!TrySave()) return; StopAudio(); _session = _store.Load(id); RememberProject(); Refresh();
        _ = WarmSelectedModelAsync();
    }
    private async Task ExportAsync()
    {
        if (_busy) return;
        try
        {
        _saveTimer.Stop(); _store.Save(_session);
        var included = _session.Rows.Where(r => r.Included).ToArray();
        if (included.Length == 0 || included.Any(r => !r.Confirmed || !r.CanConfirm)) throw new InvalidDataException("SpeechAssets.ReviewRequired");
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "ZIP (*.zip)|*.zip", FileName = Path.GetFileNameWithoutExtension(_session.SourceName) + ".zip" };
        if (dialog.ShowDialog(_owner) != true) return;
        Busy(true); StopAudio(); _cancel = CancellationTokenSource.CreateLinkedTokenSource(ApplicationBackgroundOperations.ExitToken);
        await Task.Run(() => _store.ExportAsync(_session, dialog.FileName, _cancel.Token,
            (current, total) => Dispatcher.Invoke(() => _status.Text = string.Format(T("Exporting"), current, total))));
        _status.Text = T("Exported");
        }
        catch (Exception e) { _status.Text = e is OperationCanceledException ? T("Stopped") : Error(e); }
        finally { _cancel?.Dispose(); _cancel = null; Busy(false); }
    }
    internal void Refresh()
    {
        _source.Text = string.IsNullOrEmpty(_session.SourcePath) ? _session.SourceName : _session.SourcePath;
        _voice.Text = (VideoNarratorCatalog.Models.FirstOrDefault(m => m.Id == _session.Voice.Id)?.Name ?? _session.Voice.Id) + " · " + _session.Voice.Options.Speaker
            + " · " + _session.Voice.Language.ToUpperInvariant() + " · " + _session.Voice.Rate + "%";
        BindRows(); Counts();
        RefreshProject();
    }
    private void Counts()
    {
        var rows = _session.Rows.Where(r => r.Included).ToArray();
        _counts.Text = string.Format(T("Counts"), rows.Length, rows.Count(r => r.Takes.Any(t => t.Text == r.Text)),
            rows.Count(r => r.Confirmed), rows.Count(r => !r.Confirmed));
    }
    private void Busy(bool busy)
    {
        _busy = busy; foreach (var command in _commands) command.IsEnabled = !busy;
        _format.IsEnabled = _bitrate.IsEnabled = !busy;
        Refresh();
    }
    internal void StopAudio() { _player.Stop(); _player.Close(); _playTimer.Stop(); _playingSlider = null; }
    internal void Play(SpeechAssetTake take, Slider slider)
    {
        StopAudio(); var file = _store.FileFor(_session.Id, take.File);
        if (SpeechAssetStore.Hash(file) != take.Sha256) throw new InvalidDataException("SpeechAssets.AudioChanged");
        _player.Open(new Uri(file)); _playingSlider = slider; _player.Play(); _playTimer.Start();
    }
    internal void Seek(Slider slider)
    { if (_playingSlider == slider && slider.IsMouseCaptureWithin) { _seeking = true; _player.Position = TimeSpan.FromSeconds(slider.Value); _seeking = false; } }
    private async Task PauseAsync()
    {
        try { if (_busy && ApplicationBackgroundOperations.Current is { } current) await current.PauseAsync(); }
        catch (Exception e) { _status.Text = Error(e); }
    }
    private async Task ResumePendingAsync()
    {
        try
        {
            if (ApplicationBackgroundOperations.Current is { HasPending: true } current && current.State?.Kind == SpeechAssetWork.Kind)
                await current.ResumeAsync(CancellationToken.None);
            else await GenerateAsync(null);
        }
        catch (Exception e) { _status.Text = Error(e); }
    }
}
