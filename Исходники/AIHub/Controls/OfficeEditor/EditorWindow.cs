using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Services;
using AIHub.Services.OfficeEditor;
using AIHub.Controls.OfficeReader;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Orientation = System.Windows.Controls.Orientation;
using Brushes = System.Windows.Media.Brushes;
using Brush = System.Windows.Media.Brush;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace AIHub.Controls.OfficeEditor;

public sealed partial class EditorWindow : Window
{
    private readonly Func<string, string> _l;
    private readonly EditorSession _session;
    private readonly EditorNativeHost _host = new();
    private readonly Grid _root = new();
    private readonly TextBlock _status = new(), _statistics = new(), _page = new();
    private readonly WrapPanel _toolbar = new();
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private readonly DispatcherTimer _resize = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly CancellationTokenSource _lifetime = new();
    private Button _captureButton = null!, _speechButton = null!;
    private bool _ready, _polling, _closing, _closed, _dark, _captureRich = true;
    private string? _savePath;
    private int _pages = 1;
    private int _zoom = 100;
    private long _revision = -1;
    private string? _spellingWarning;
    private Task _startup = Task.CompletedTask;
    internal static readonly List<EditorWindow> Windows = [];
    internal static EditorWindow? CaptureOwner { get; private set; }
    internal string? SourcePath => _session.Draft.Source;
    internal string? SavedPath => _savePath;
    internal string DraftDirectory => _session.Draft.DirectoryPath;
    internal bool IsDarkTheme => _dark;

    public EditorWindow(Window? owner, Func<string, string> localize, bool dark, string? source = null, string? recoveryId = null)
    {
        _l = localize; _dark = dark; _session = new(source); _savePath = source;
        if (owner is not null) { Owner = owner; Icon = owner.Icon; }
        Width = 1280; Height = 880; MinWidth = 760; MinHeight = 480; FontSize = 14;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        Resources.MergedDictionaries.Add(new() { Source = new Uri("/AIHub;component/Controls/OfficeReader/ReaderResources.xaml", UriKind.Relative) });
        Resources.MergedDictionaries.Add(new() { Source = new Uri("/AIHub;component/Controls/OfficeEditor/EditorResources.xaml", UriKind.Relative) });
        ApplyTheme(); Build(); Content = _root; Windows.Add(this); UpdateTitle();
        AutomationProperties.SetAutomationId(this, "Office.Editor.Window");
        _host.Resized += () => { if (_ready) _resize.Start(); };
        _host.FocusRequested += () => _ = RunAsync(new { action = "focus" });
        _resize.Tick += async (_, _) => { _resize.Stop(); var size = _host.PixelSize(); await RunAsync(new { action = "resize", width = size.Width, height = size.Height }); };
        _poll.Tick += async (_, _) => await PollAsync();
        Loaded += async (_, _) => await (_startup = StartAsync(recoveryId));
        Closing += ClosingWindow;
        SourceInitialized += (_, _) => WindowTitleBarThemeService.Apply(this, _dark);
        PreviewKeyDown += (_, e) => {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            if (e.Key == Key.S) { _ = SaveAsync(); e.Handled = true; }
            else if (e.Key == Key.O) { SelectFile(); e.Handled = true; }
            else if (e.Key == Key.N) { NewDocument(); e.Handled = true; }
        };
    }
    internal static EditorWindow Open(Window? owner, Func<string, string> l, bool dark, string? path = null)
    {
        var existing = path is null ? null : Windows.FirstOrDefault(w => string.Equals(w.SourcePath, path, StringComparison.OrdinalIgnoreCase) || string.Equals(w.SavedPath, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) { existing.WindowState = WindowState.Normal; existing.Activate(); return existing; }
        var window = new EditorWindow(owner, l, dark, path); window.Show(); return window;
    }
    private string T(string key) => _l("OfficeEditor." + key);
    private Button Action(string caption, string id, Action action, string? tip = null)
    {
        var button = new Button { Content = caption, ToolTip = tip ?? caption };
        AutomationProperties.SetAutomationId(button, "Office.Editor." + id); AutomationProperties.SetName(button, tip ?? caption);
        button.Click += (_, _) => action(); return button;
    }
    private async Task StartAsync(string? recoveryId)
    {
        _status.Text = T("Loading"); _toolbar.IsEnabled = false;
        try
        {
            string? recovery = null;
            if (recoveryId is not null && Guid.TryParseExact(recoveryId, "N", out _))
                recovery = Path.Combine(EditorDraft.Root, recoveryId, "document.odt");
            var size = _host.PixelSize();
            var result = await _session.StartAsync(_host.HandleValue, size.Width, size.Height, _l("Navigation.Creation") == "Создание", recovery, _lifetime.Token);
            if (_closed) return;
            _host.DocumentHandle = (nint)result.GetProperty("hwnd").GetInt64();
            var languages = result.GetProperty("spellingLanguages").EnumerateArray().Select(x => x.GetString()).ToArray();
            var missing = new[] { "ru-RU", "en-US" }.Except(languages).ToArray();
            if (missing.Length > 0) _spellingWarning = string.Format(T("SpellingUnavailable"), string.Join(", ", missing));
            _ready = true; _toolbar.IsEnabled = true; _poll.Start(); UpdateTitle();
            _status.Text = _spellingWarning ?? T("DraftSaved"); await RunAsync(new { action = "focus" });
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { Failure(error); }
    }
    private async Task<JsonElement?> RunAsync(object command)
    {
        if (!_ready || _closed || _closing) return null;
        try { return await _session.RequestAsync(command, _lifetime.Token); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { if (!_closed) Failure(error); return null; }
    }
    private void Failure(Exception error)
    {
        ComponentAuditFailures.Report(error);
        OwnedProcessRegistry.Log("office_editor_failed", "Office.Editor", detail: error.ToString());
        _status.Text = error.Message.StartsWith("OfficeEditor.", StringComparison.Ordinal) ? _l(error.Message) : T("Failed");
    }
    private void UpdateTitle() => Title = (Path.GetFileName(_savePath ?? SourcePath) ?? T("NewDocument")) + " · " + T("Title");
    private async Task PollAsync()
    {
        if (_polling || !_ready || _closed || _closing) return; _polling = true;
        try
        {
            var result = await RunAsync(new { action = "state" }); if (result is not { } state) return;
            _pages = state.GetProperty("pages").GetInt32(); _page.Text = state.GetProperty("page").GetInt32() + " / " + _pages;
            if (state.TryGetProperty("action", out var pending))
            {
                switch (pending.GetString())
                {
                    case "save": await SaveAsync(); break;
                    case "saveAs": await SaveAsync(true); break;
                    case "open": SelectFile(); break;
                    case "new": NewDocument(); break;
                    case "close": Close(); return;
                }
            }
            var revision = state.GetProperty("revision").GetInt64();
            var error = state.GetProperty("draftError").GetString();
            if (!string.IsNullOrEmpty(error)) _status.Text = T("DraftFailed");
            else if (state.GetProperty("savedRevision").GetInt64() >= revision) _status.Text = _spellingWarning ?? T("DraftSaved");
            if (revision != _revision)
            {
                _revision = revision;
                var text = await RunAsync(new { action = "text", selection = false });
                if (text is { } value)
                {
                    var stats = Services.OfficeReader.ReaderTextStatistics.Count(value.GetString());
                    _statistics.Text = string.Format(_l("Reader.Statistics"), stats.Lines, stats.Characters, stats.ApproximateTokens);
                }
            }
        }
        finally { _polling = false; }
    }
    private async void ClosingWindow(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closed) return; e.Cancel = true; if (_closing) return; _closing = true;
        StopCapture(); _poll.Stop(); _resize.Stop();
        if (!_ready) _lifetime.Cancel();
        await _startup;
        await _captureTask;
        await CloseSpeechAsync();
        try
        {
            if (_ready) await _session.RequestAsync(new { action = "save" });
        }
        catch (Exception error) { Failure(error); }
        finally
        {
            _closed = true; _lifetime.Cancel(); await _session.DisposeAsync(); Windows.Remove(this);
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
    }
    private void ApplyTheme()
    {
        SolidColorBrush Color(string value) => new((System.Windows.Media.Color)ColorConverter.ConvertFromString(value));
        Resources["ReaderText"] = Color(_dark ? "#F1F4FA" : "#202735"); Resources["ReaderPanel"] = Color(_dark ? "#172132" : "#FFFFFF");
        Resources["ReaderLine"] = Color(_dark ? "#3A465C" : "#CCD3DF"); Resources["ReaderHover"] = Color(_dark ? "#293954" : "#E7EFFF");
        Background = Color(_dark ? "#0D1523" : "#EDF0F6"); Foreground = (Brush)Resources["ReaderText"];
        Resources["WindowBackgroundBrush"] = Background; Resources["TextPrimaryBrush"] = Foreground; Resources["TextSecondaryBrush"] = Foreground;
        Resources["PanelBrush"] = Resources["InputBrush"] = Resources["SecondaryButtonBackgroundBrush"] = Resources["ReaderPanel"];
        Resources["LineBrush"] = Resources["ReaderLine"]; Resources["AccentBrush"] = Color("#2563EB"); Resources["UiBodyFontSize"] = 14d;
        WindowTitleBarThemeService.Apply(this, _dark);
    }
}
