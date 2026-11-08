using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Services;
using AIHub.Services.Dictation;
using AIHub.Services.OfficeEditor;
using Button = System.Windows.Controls.Button;
using UserControl = System.Windows.Controls.UserControl;
using Brush = System.Windows.Media.Brush;

namespace AIHub.Controls.OfficeTextEditor;

internal sealed partial class OfficeTextEditorView : UserControl, IAsyncDisposable
{
    private readonly Window _owner;
    private readonly Func<string,string> _l;
    private OfficeTextBrowser _browser = new();
    private readonly Grid _root = new();
    private readonly WrapPanel _toolbar = new();
    private readonly TextBlock _status = new(), _page = new(), _statistics = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _autosave = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private readonly SemaphoreSlim _storage = new(1,1);
    private OfficeTextWork? _work;
    private long _revision, _checkpoint;
    private bool _ready, _closed, _closing, _discardOnDispose, _dark;
    private int _zoom = 100, _pages = 1;
    private Button _captureButton = null!, _speechButton = null!;
    internal event Action? ReadRequested;
    internal event Action<string?,string?>? OpenRequested;
    internal event Action? TitleChanged;
    internal string DocumentTitle => (Path.GetFileName(TargetPath ?? SourcePath) ?? T("NewDocument")) + " · " + T("Title");
    internal string? SourcePath => _work?.SourcePath;
    internal string? TargetPath => _work?.TargetPath;
    internal bool IsDarkTheme => _dark;
    internal int CurrentPage { get; private set; }
    internal async Task GoToPageAsync(int zeroBased)
    {
        if (!_ready || _closed) return;
        if (_nativeView != null && _currentSheet?.Kind == OfficeSheetKind.Presentation) _nativeView.GoToPage(zeroBased);
        else if (_draftSlideList is { Items.Count: > 0 } list) list.SelectedIndex = Math.Clamp(zeroBased, 0, list.Items.Count - 1);
        else if (IsTextSheet) await RunAsync(new { action = "gotoPage", page = Math.Max(0,zeroBased) + 1 });
    }
    private string T(string key) => _l("OfficeEditor." + key);
    private bool IsRussian => _l("Navigation.Creation") == "Создание";
    private OfficeTextWork Work => _work ?? throw new InvalidOperationException("OfficeEditor.EngineUnavailable");

    internal OfficeTextEditorView(Window owner, Func<string,string> localize, bool dark)
    {
        _owner = owner; _l = localize;
        Resources.MergedDictionaries.Add(new() { Source = new Uri("/AIHub;component/Controls/OfficeReader/ReaderResources.xaml", UriKind.Relative) });
        Resources.MergedDictionaries.Add(new() { Source = new Uri("/AIHub;component/Controls/OfficeEditor/EditorResources.xaml", UriKind.Relative) });
        SetTheme(dark);
        Build();
        _workspace.Children.Add(_root); Content = _workspace;
        _toolbar.IsEnabled = false;
        _browser.IsEnabled = false;
        _status.Text = T("Loading");
        AutomationProperties.SetAutomationId(this, "Office.Editor.View");
        _autosave.Tick += async (_, _) =>
        {
            _autosave.Stop();
            if (_closed || _closing || !_ready || _review != null || _reviewOpening) return;
            await GuardAsync(() => WithStorageAsync(CheckpointAsync));
        };
        RegisteredDictationTarget.Register(_browser, this);
    }

    internal async Task InitializeAsync(string? source, CancellationToken token, string? recoveryId = null,
        OfficeSheetKind firstKind = OfficeSheetKind.Text)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, token);
        var ct = linked.Token;
        await PrepareSymbolSuggestionsAsync(ct);
        _work = await OfficeTextWork.OpenAsync(source, ct, recoveryId, firstKind);
        ct.ThrowIfCancellationRequested();
        await DisplaySheetAsync(Work.Sheets[0], ct);
        if (CheckSourceCompatibility && Work.CompatibilityBaseline == null && !Work.CompatibilityBaselineUnavailable)
        {
            var baseline = await _browser.RequestAsync(new { action = "compatibilityBaseline" },ct);
            var signature = baseline.GetString() ?? throw new InvalidDataException("OfficeEditor.DraftFailed");
            await Task.Run(() => Work.SetCompatibilityBaseline(signature),ct);
        }
        if (Work.CompatibilityBaselineUnavailable)
            await Work.SetFormatReasonAsync(Work.FormatReason ?? T("RecoveryCompatibilityUnknown"),ct);
        _ready = true; _toolbar.IsEnabled = true; _browser.IsEnabled = true;
        _status.Text = Work.SourceNeedsCopy ? T("SourceNeedsCopy") : T("DraftSaved");
        if (IsTextSheet) _browser.Focus();
        RefreshProjectNotice();
        TitleChanged?.Invoke();
    }

    private void SurfaceMessage(JsonElement message)
    {
        if (_closed || !IsTextSheet) return;
        switch (message.GetProperty("type").GetString())
        {
            case "state":
                var previous = _revision;
                SetState(message);
                if (_ready && _revision > previous && CheckSourceCompatibility)
                    _ = GuardAsync(() => UpdateCompatibilityAsync(_revision));
                break;
            case "spellCheck": ScheduleSpelling(message); break;
            case "symbolSuggest": _ = SendSymbolSuggestionsAsync(message, _browser); break;
            case "selectionAction":
                var selectionBrowser = _browser;
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (ReferenceEquals(_browser, selectionBrowser)) _ = GuardAsync(() => SelectionActionAsync(message));
                }));
                break;
            case "shortcut" when _ready && !_closing:
                switch (message.GetProperty("name").GetString())
                {
                    case "save": _ = GuardAsync(() => SaveFromUiAsync(false)); break;
                    case "saveAs": _ = GuardAsync(() => SaveFromUiAsync(true)); break;
                    case "new": OpenRequested?.Invoke(null, null); break;
                    case "open": SelectFile(); break;
                    case "close": _owner.Close(); break;
                }
                break;
        }
    }
    private void SetState(JsonElement state)
    {
        if (state.ValueKind != JsonValueKind.Object) return;
        if (state.TryGetProperty("pages", out var pages) && pages.TryGetInt32(out var count)) _pages = Math.Max(1,count);
        if (state.TryGetProperty("page", out var page) && page.TryGetInt32(out var number))
        {
            CurrentPage = Math.Clamp(number - 1, 0, _pages - 1);
            _page.Text = (CurrentPage + 1) + " / " + _pages;
        }
        if (state.TryGetProperty("characters", out var length) && state.TryGetProperty("words",out var words))
            _statistics.Text = string.Format(T("TextCounts"), words, length);
        if (!state.TryGetProperty("revision", out var rev) || !rev.TryGetInt64(out var value)) return;
        _revision = Math.Max(_revision, value);
        if (_ready && !_closing && _revision > _checkpoint && !_autosave.IsEnabled) _autosave.Start();
    }
    private async Task WithStorageAsync(Func<Task> operation)
    {
        await _storage.WaitAsync(_lifetime.Token);
        try { await operation(); }
        finally { _storage.Release(); }
    }
    private async Task CheckpointAsync()
    {
        if (!_ready || _closed) return;
        var token = _lifetime.Token;
        if (!IsTextSheet)
        {
            await SaveSheetContentAsync(token);
            return;
        }
        SetState(await _browser.RequestAsync(new { action = "state" }, token));
        await UpdateCompatibilityAsync(_revision);
        if (_revision <= _checkpoint) return;
        var before = _revision;
        var snapshot = await _browser.RequestAsync(new { action = "export", format = "docx" }, token);
        var bytes = Convert.FromBase64String(snapshot.GetProperty("base64").GetString()!);
        await Work.WriteSheetAsync(CurrentSheetId!, bytes, token);
        _checkpoint = snapshot.TryGetProperty("revision", out var revision) ? revision.GetInt64() : before;
        if (_revision > _checkpoint && !_closing) _autosave.Start();
        if (!Work.NeedsProject && _missingDictionaries.Count == 0) _status.Text = T("DraftSaved");
    }

    private async Task<JsonElement?> RunAsync(object command)
    {
        if (!_ready || _closed || _closing || !IsTextSheet) return null;
        try { return await _browser.RequestAsync(command, _lifetime.Token); }
        catch (Exception error) when (error is not OutOfMemoryException) { Failure(error); return null; }
    }
    private async Task GuardAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) when (error is not OutOfMemoryException) { Failure(error); }
    }
    private void Failure(Exception error)
    {
        if (_closed || error is OperationCanceledException && _lifetime.IsCancellationRequested) return;
        ComponentAuditFailures.Report(error);
        OwnedProcessRegistry.Log("office_text_editor_failed", "Office.Editor", detail: error.ToString());
        _status.Text = error.Message.StartsWith("OfficeEditor.", StringComparison.Ordinal) ? _l(error.Message) : T("Failed");
        _status.ToolTip = error.Message;
    }
    internal void ResumeEditing()
    {
        if (!_ready || _closed) return;
        if (!IsTextSheet) { _sheetSurface.Focus(); return; }
        _browser.Focus();
        _ = RunAsync(new { action = "focus" });
    }

    internal void SetTheme(bool dark)
    {
        _dark = dark;
        SolidColorBrush Color(string value) => new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value));
        Resources["ReaderText"] = Color(dark ? "#F1F4FA" : "#202735");
        Resources["ReaderPanel"] = Color(dark ? "#172132" : "#FFFFFF");
        Resources["ReaderLine"] = Color(dark ? "#3A465C" : "#CCD3DF");
        Resources["ReaderHover"] = Color(dark ? "#293954" : "#E7EFFF");
        Background = Color(dark ? "#0D1523" : "#EDF0F6"); Foreground = (Brush)Resources["ReaderText"];
        if (_ready) _ = RunAsync(new { action = "theme", dark });
    }

    public async ValueTask DisposeAsync()
    {
        if (_closed) return;
        if (_summaryView != null) await _summaryView.DisposeAsync();
        _closed = true; _autosave.Stop(); StopCapture(); _lifetime.Cancel();
        CloseNativeSymbolSuggestions();
        _owner.Deactivated -= HideNativeSymbolSuggestions;
        if (_review != null) await CloseReviewAsync(_review);
        foreach (var surface in _sheetSurface.Children.OfType<OfficeTextBrowser>().ToArray())
            surface.CloseSurface();
        await _captureTask;
        await CloseSpeechAsync();
        await _storage.WaitAsync();
        try
        {
            _workbook?.Dispose();
            if (_nativeView != null) await _nativeView.DisposeAsync();
            await CloseDraftCanvasAsync();
            _work?.Close(_discardOnDispose || !_ready && _work is { IsDirty: false });
        }
        finally { _storage.Release(); _lifetime.Dispose(); }
    }
}
