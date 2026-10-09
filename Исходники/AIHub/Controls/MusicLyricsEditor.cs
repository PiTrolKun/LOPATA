using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using AIHub.Services;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace AIHub.Controls;

public sealed class MusicLyricsEditor : UserControl, IDisposable
{
    private readonly Func<string, CancellationToken, Task<IMusicTokenizer>> _loader;
    private readonly TextBlock _title = new(), _context = new(), _weight = new(), _characters = new(), _hint = new();
    private readonly DispatcherTimer _debounce;
    private readonly TextBox _editor;
    private Func<string, string> _l = key => key;
    private IMusicTokenizer? _tokenizer;
    private CancellationTokenSource? _loadCancel, _countCancel;
    private string _modelPath = "", _tags = "", _instruction = MusicTextBudget.DefaultInstruction;
    private int _outputReserve, _revision;
    private bool _disposed, _instrumental, _ace, _counting = true;
    private string _stateKey = "Music.Editor.Loading";
    private string _externalHintKey = "Music.Ace.EditorHint";
    public MusicTextUsage? Usage { get; private set; }
    public bool CanGenerate => !_disposed && (_ace ? _instrumental || !string.IsNullOrWhiteSpace(_editor.Text) : MusicTextBudget.CanStart(Usage, _counting, _editor.Text, _instrumental));
    public event EventHandler? ValidityChanged;
    public event EventHandler? SelectionChanged;
    public string Lyrics { get => _editor.Text; set => _editor.Text = value; }
    public MusicTextSelection CaptureSelection() => new(_editor.Text, _editor.SelectionStart, _editor.SelectionLength);
    public bool ApplyEdit(MusicTextSelection source, MusicTextEdit? edit)
    {
        if (_disposed || edit is null || _editor.Text != source.Text) return false;
        _editor.BeginChange();
        try
        {
            _editor.Select(edit.Start, edit.RemoveLength);
            _editor.SelectedText = edit.Insert;
            _editor.Select(edit.SelectionStart, edit.SelectionLength);
        }
        finally { _editor.EndChange(); }
        _editor.Focus(); return true;
    }

    public MusicLyricsEditor() : this((path, token) => Task.Run<IMusicTokenizer>(() => path.EndsWith(".tiktoken", StringComparison.Ordinal)
        ? new MusicBf16Tokenizer(path, token) : new MusicTokenizer(MusicTokenizerMetadata.Read(path, token)), token)) { }
    public MusicLyricsEditor(Func<string, CancellationToken, Task<IMusicTokenizer>> loader)
    {
        _loader = loader;
        var grid = new Grid { Margin = new Thickness(12) };
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _title.FontSize = 20; _title.FontWeight = FontWeights.SemiBold; _title.Margin = new(0, 0, 0, 8);
        _title.TextAlignment = TextAlignment.Center;
        _title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush"); grid.Children.Add(_title);
        _editor = new TextBox { AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            FontSize = 18, BorderThickness = new(0), Padding = new(4), IsUndoEnabled = true, UndoLimit = 1000 };
        _editor.SetResourceReference(BackgroundProperty, "PanelBrush");
        _editor.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        _editor.SetResourceReference(TextBox.CaretBrushProperty, "TextPrimaryBrush");
        AutomationProperties.SetAutomationId(_editor, "Music.Lyrics");
        Grid.SetRow(_editor, 1); grid.Children.Add(_editor);
        var footer = new StackPanel { Margin = new(0, 10, 0, 0) };
        foreach (var text in new[] { _context, _weight, _characters, _hint })
        {
            text.FontSize = 12; text.TextWrapping = TextWrapping.Wrap; text.Margin = new(0, 2, 0, 2);
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush"); footer.Children.Add(text);
        }
        AutomationProperties.SetAutomationId(_context, "Music.ContextCount");
        AutomationProperties.SetAutomationId(_weight, "Music.LyricsCount");
        AutomationProperties.SetAutomationId(_characters, "Music.CharacterCount");
        Grid.SetRow(footer, 2); grid.Children.Add(footer); Content = grid;
        _debounce = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(180) };
        _debounce.Tick += DebounceTick;
        _editor.TextChanged += (_, _) => InvalidateCounts();
        _editor.SelectionChanged += (_, _) => SelectionChanged?.Invoke(this, EventArgs.Empty);
        UpdateLabels();
    }

    public void Localize(Func<string, string> localize) { _l = localize; UpdateLabels(); }
    public void AttachHistory(MusicHistoryControl history)
    {
        var grid = (Grid)Content; grid.Children.Remove(_title); _title.FontSize = 18; _title.TextWrapping = TextWrapping.Wrap;
        _title.Margin = new(3, 0, 3, 8); Grid.SetColumn(_title, 1); history.Children.Add(_title); grid.Children.Add(history);
    }
    public void ResetTokenizer()
    {
        _modelPath = ""; _loadCancel?.Cancel(); _countCancel?.Cancel(); _tokenizer = null;
        _revision++; Usage = null; _counting = true; _stateKey = "Music.Editor.Unavailable";
        _debounce.Stop(); UpdateLabels(); ValidityChanged?.Invoke(this, EventArgs.Empty);
    }
    public Task LoadTokenizerAsync(string path) => Dispatcher.InvokeAsync(() => LoadTokenizerCoreAsync(path)).Task.Unwrap();
    private async Task LoadTokenizerCoreAsync(string path)
    {
        if (_disposed || path == _modelPath && (_tokenizer is not null || _loadCancel is not null)) return;
        _modelPath = path; _loadCancel?.Cancel(); _countCancel?.Cancel(); _tokenizer = null;
        Usage = null; _counting = true; _stateKey = "Music.Editor.Loading"; UpdateLabels(); ValidityChanged?.Invoke(this, EventArgs.Empty);
        using var cancel = new CancellationTokenSource(); _loadCancel = cancel;
        try
        {
            var tokenizer = await _loader(path, cancel.Token);
            cancel.Token.ThrowIfCancellationRequested();
            if (_disposed || _loadCancel != cancel) return;
            _tokenizer = tokenizer; await RefreshCountsAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!_disposed && _loadCancel == cancel)
            { _stateKey = "Music.Editor.Unavailable"; Usage = null; _counting = false; UpdateLabels(); ValidityChanged?.Invoke(this, EventArgs.Empty); }
        }
        finally { if (_loadCancel == cancel) _loadCancel = null; }
    }

    public void ConfigureRequest(string tags, string instruction, int outputReserve = 0, bool instrumental = false, bool ace = false,
        string externalHintKey = "Music.Ace.EditorHint")
    {
        if (outputReserve < 0) throw new ArgumentOutOfRangeException(nameof(outputReserve));
        if (ace != _ace) { ResetTokenizer(); _ace = ace; }
        _externalHintKey = externalHintKey;
        _tags = tags; _instruction = instruction; _outputReserve = outputReserve; _instrumental = instrumental; InvalidateCounts();
    }
    // Every future launch handler must take this snapshot, not read the textbox directly.
    public bool TryGetGenerationText(out string lyrics)
    { lyrics = CanGenerate && !_instrumental ? _editor.Text : ""; return CanGenerate; }

    private void InvalidateCounts()
    {
        if (_disposed) return;
        _revision++; _countCancel?.Cancel(); Usage = null; _counting = true;
        if (_ace) { _debounce.Stop(); _counting = false; _stateKey = _externalHintKey; UpdateLabels(); ValidityChanged?.Invoke(this, EventArgs.Empty); return; }
        _stateKey = _tokenizer is null ? _loadCancel is null ? "Music.Editor.Unavailable" : "Music.Editor.Loading" : "Music.Editor.Counting";
        UpdateLabels(); ValidityChanged?.Invoke(this, EventArgs.Empty);
        _debounce.Stop(); if (_tokenizer is not null) _debounce.Start();
    }
    private async void DebounceTick(object? sender, EventArgs e)
    { _debounce.Stop(); await RefreshCountsAsync(); }

    public Task RefreshCountsAsync() => Dispatcher.InvokeAsync(RefreshCountsCoreAsync).Task.Unwrap();
    private async Task RefreshCountsCoreAsync()
    {
        _debounce.Stop(); if (_disposed || _tokenizer is null) return;
        _countCancel?.Cancel(); using var cancel = new CancellationTokenSource(); _countCancel = cancel;
        var revision = _revision; var tokenizer = _tokenizer; var text = _editor.Text;
        var tags = _tags; var instruction = _instruction; var reserve = _outputReserve; var instrumental = _instrumental;
        _counting = true; _stateKey = "Music.Editor.Counting"; UpdateLabels(); ValidityChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            var usage = await Task.Run(() => MusicTextBudget.Measure(tokenizer, text, tags, instruction, reserve, cancel.Token, instrumental), cancel.Token);
            cancel.Token.ThrowIfCancellationRequested();
            if (_disposed || revision != _revision || _countCancel != cancel) return;
            Usage = usage; _counting = false; _stateKey = usage.Exceeded ? "Music.Editor.Exceeded" : "";
            UpdateLabels(); ValidityChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!_disposed && revision == _revision && _countCancel == cancel)
            { Usage = null; _counting = false; _stateKey = "Music.Editor.Unavailable"; UpdateLabels(); ValidityChanged?.Invoke(this, EventArgs.Empty); }
        }
        finally { if (_countCancel == cancel) _countCancel = null; }
    }

    private void UpdateLabels()
    {
        _title.Text = _l("Music.Editor.Title");
        _context.Visibility = _weight.Visibility = _ace ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(_editor, _title.Text);
        _context.Text = string.Format(_l("Music.Editor.Context"), Usage?.TotalTokens.ToString("N0") ?? "—", MusicTextBudget.ContextSize.ToString("N0"));
        _weight.Text = string.Format(_l("Music.Editor.Weight"), Usage?.LyricsTokens.ToString("N0") ?? "—", Usage?.LyricsBudget.ToString("N0") ?? "—");
        _characters.Text = string.Format(_l("Music.Editor.Characters"), MusicTextBudget.CharacterCount(_editor.Text).ToString("N0"));
        _hint.Text = string.IsNullOrEmpty(_stateKey) ? "" : _l(_stateKey);
        _hint.Visibility = string.IsNullOrEmpty(_stateKey) ? Visibility.Collapsed : Visibility.Visible;
        if (Usage?.Exceeded == true)
        {
            _editor.Foreground = System.Windows.Media.Brushes.Red;
            _context.Foreground = _weight.Foreground = System.Windows.Media.Brushes.Red;
        }
        else
        {
            _editor.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
            _context.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            _weight.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _debounce.Stop(); _debounce.Tick -= DebounceTick; _loadCancel?.Cancel(); _countCancel?.Cancel();
    }
}
