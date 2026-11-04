using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using AIHub.Services;
using AIHub.Services.VideoEditor;
using AIHub.Services.VideoUpscale;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using ProgressBar = System.Windows.Controls.ProgressBar;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Panel = System.Windows.Controls.Panel;
using MessageBox = System.Windows.MessageBox;

namespace AIHub.Controls.VideoUpscale;

internal sealed partial class UpscaleWindow : Window
{
    private readonly Func<string, string> _l;
    private UpscaleProject _project;
    private readonly VideoEditor.VideoPreview _original = new(), _result = new();
    private readonly StackPanel _software = new() { Orientation = Orientation.Horizontal }, _ai = new() { Orientation = Orientation.Horizontal };
    private readonly WrapPanel _toolbar = new(), _aiCommands = new();
    private readonly WrapPanel _actions = new();
    private readonly TextBlock _status = new() { TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap }, _clock = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new(8, 0, 16, 0), TextWrapping = TextWrapping.NoWrap }, _rangeText = new() { TextWrapping = TextWrapping.NoWrap }, _sourceText = new() { TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap }, _previewText = new() { TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
    private readonly TextBlock _chain = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _log = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 100 };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, Height = 5 };
    private readonly Slider _position = new() { Minimum = 0, Maximum = 5 }, _rangeStart = new() { Minimum = 0, Maximum = 1 }, _rangeLength = new() { Minimum = .1, Maximum = 5, Value = 5, Width = 180 };
    private readonly UpscaleRange _range = new();
    private readonly System.Windows.Threading.DispatcherTimer _rangeRefresh = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly System.Windows.Threading.DispatcherTimer _sync = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _syncRunning;
    internal TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _operation;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _busy, _loading, _playing, _seeking, _initialized, _closed;
    private string? _sourceProxy;
    private double _at;
    internal Func<Task>? EnsureRuntime { get; set; }
    internal Func<UpscaleProject, CancellationToken, Task<string?>>? PrepareModels { get; set; }
    internal UpscaleProject Project => _project;
    private string T(string key) => _l("VideoUpscale." + key);
    internal UpscaleWindow(Window owner, Func<string, string> localize, UpscaleProject? project = null)
    {
        Owner = owner; _l = localize; _project = project ?? new();
        MusicWishUi.PrepareWindow(this, T("Title"), "VideoUpscale.Window", 1500);
        if (!Resources.MergedDictionaries.Contains(owner.Resources)) Resources.MergedDictionaries.Insert(0, owner.Resources);
        Height = 950; MinWidth = 1050; MinHeight = 720; UseLayoutRounding = true; SnapsToDevicePixels = true;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/VideoEditor/VideoEditorResources.xaml", UriKind.Relative) });
        Content = Layout();
        Loaded += (_, _) => Safe(async () => {
            if (_initialized) return;
            _initialized = true;
            if (project is null) { var selected = SelectMode(); if (selected is null) { Ready.TrySetCanceled(); Close(); return; } SetMode(selected.Value); }
            if (EnsureRuntime is not null) await EnsureRuntime();
            await UpscaleFfmpeg.PrepareAsync(_lifetime.Token);
            await _original.InitializeAsync(_lifetime.Token); await _result.InitializeAsync(_lifetime.Token);
            NormalizeProject(); RefreshSteps(); if (File.Exists(_project.Source)) await RefreshMedia();
            Ready.TrySetResult();
        });
        _original.PositionChanged += at => {
            if (_seeking) return; _at = at; _loading = true; _position.Value = at; _loading = false; Clock();
            if (Math.Abs(_at - _project.PreviewLength) < .03) _playing = false;
        };
        _position.ValueChanged += (_, _) => { if (!_loading) Safe(() => Seek(_position.Value)); };
        _rangeStart.ValueChanged += (_, _) => RangeChanged(); _rangeLength.ValueChanged += (_, _) => RangeChanged();
        _range.Changed += (start, length) => { _loading = true; _rangeLength.Value = length; _rangeStart.Maximum = Math.Max(0, _project.Media.Duration - length); _rangeStart.Value = start; _loading = false; RangeChanged(); };
        _rangeRefresh.Tick += (_, _) => { _rangeRefresh.Stop(); if (!_busy && !_closed) Safe(OriginalProxy); };
        _sync.Tick += async (_, _) => { if (!_playing || _syncRunning || _closed) return; _syncRunning = true; try { await _result.SeekAsync(_at); } catch (Exception error) { VideoErrors.Report(error); } finally { _syncRunning = false; } };
        _sync.Start();
        SizeChanged += (_, _) => { if (_initialized && !_closed) Safe(ApplyView); };
        Closing += (_, e) => {
            Save(); if (_busy && !ApplicationBackgroundOperations.ExitToken.IsCancellationRequested) { e.Cancel = true; Hide(); return; }
            _closed = true; _sync.Stop(); _rangeRefresh.Stop(); _lifetime.Cancel(); _original.Dispose(); _result.Dispose();
        };
        RefreshSteps();
    }
    private void Save()
    {
        _chain.Text = T("Chain") + ": " + string.Join(" → ", _project.Steps.Select((s, i) => s.Enabled ? (i + 1) + ". " + T("Engine." + s.Engine) : null).OfType<string>());
        if (_loading || _closed || string.IsNullOrWhiteSpace(_project.Source)) return;
        UpscaleStore.SaveProject(_project);
        _previewText.Text = _project.PreviewPath is not null && _project.PreviewFingerprint == PreviewFingerprint() ? T("PreviewCurrent") : T("PreviewStale");
    }
    private void NormalizeProject()
    {
        UpscaleCatalog.Validate(_project, allowEmptyChain: true);
        _project.Workspace = UpscaleStore.Work(_project.Id);
    }
    private async void Safe(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { if (!_closed) _status.Text = T("Cancelled"); }
        catch (Exception error) { VideoErrors.Report(error); Ready.TrySetException(error); if (!_closed) { var key = error.Message.Split([':', ' ', '('])[0]; _status.Text = _l(key); _log.AppendText(error + Environment.NewLine); MessageBox.Show(this, _status.Text, T("Title"), MessageBoxButton.OK, MessageBoxImage.Warning); } }
    }
}
