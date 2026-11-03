using System.IO;
using System.Text.Json;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AIHub.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;

namespace AIHub.Controls;

/// <summary>Offline plain-text editor. A CSS exclusion wraps the last visible lines around Send.
/// The native TextBox remains the draft buffer and the usable fallback if WebView2 is unavailable.</summary>
internal sealed partial class LetItOutComposerBrowser : WebView2, IDisposable
{
    private readonly TextBox _buffer;
    private readonly Button _send;
    private readonly TextBlock _hint;
    private readonly Action<string> _changed;
    private readonly Action _submit;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _ready, _closed, _receiving;
    private Task? _start;
    private string _lastTheme = "";
    internal bool IsReady => _ready && !_closed;
    internal Action<string[]>? FilesDropped { get; set; }
    internal Func<bool>? PasteAttachment { get; set; }

    internal LetItOutComposerBrowser(TextBox buffer, Button send, TextBlock hint, Action<string> changed, Action submit, Func<string>? scope = null)
    {
        _buffer = buffer; _send = send; _hint = hint; _changed = changed; _submit = submit;
        _scope = scope ?? (() => "composer");
        Services.Dictation.RegisteredDictationTarget.Register(this, this);
        IsVisibleChanged += (_, _) => { if (!IsVisible) _dictationEpoch++; };
        DefaultBackgroundColor = System.Drawing.Color.Transparent;
        Visibility = Visibility.Hidden;
        Loaded += (_, _) => Start();
        LayoutUpdated += UpdateAppearance;
    }

    internal void Start()
    {
        if (_start is null && IsLoaded && !_closed) _start = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        var token = _lifetime.Token;
        try
        {
            using var audit = ComponentAudit.CaptureOperation();
            await ComponentLicenseGate.EnsureAsync(["viewer.webview2", "nuget.Microsoft.Web.WebView2"], token);
            await WebView2ComponentAudit.VerifyAsync(token);
            var environment = await CoreWebView2Environment.CreateAsync(null,
                Path.Combine(AppDataPaths.BaseDirectory, "Chat", "ComposerProfile"));
            token.ThrowIfCancellationRequested();
            var options = environment.CreateCoreWebView2ControllerOptions();
            options.IsInPrivateModeEnabled = true;
            await EnsureCoreWebView2Async(environment, options);
            token.ThrowIfCancellationRequested();
            var core = CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.ProcessFailed += (_, e) => UseNative(new IOException("Chat composer process failed: " + e.ProcessFailedKind));
            var documentUri = "data:text/html;charset=utf-8;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(Document));
            ulong navigationId = 0;
            core.NavigationStarting += (_, e) =>
            {
                e.Cancel = _ready || (e.Uri != documentUri && e.Uri != "about:blank");
                if (!e.Cancel) navigationId = e.NavigationId;
            };
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) => e.Response = environment.CreateWebResourceResponse(Stream.Null, 403, "Blocked", "");
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            core.NavigationCompleted += Completed;
            try
            {
                core.Navigate(documentUri);
                await loaded.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
            }
            finally { core.NavigationCompleted -= Completed; }
            using var source = typeof(LetItOutComposerBrowser).Assembly.GetManifestResourceStream("AIHub.ChatComposerScript")!;
            using var reader = new StreamReader(source);
            await core.ExecuteScriptAsync(await reader.ReadToEndAsync(token));
            token.ThrowIfCancellationRequested();
            core.WebMessageReceived += Receive;
            _ready = true;
            await ExecuteScriptAsync(SetScript(_buffer.Text));
            _buffer.Visibility = Visibility.Hidden;
            _send.Visibility = _hint.Visibility = Visibility.Hidden;
            Visibility = Visibility.Visible;
            UpdateAppearance(this, EventArgs.Empty);
            void Completed(object? sender, CoreWebView2NavigationCompletedEventArgs e)
            {
                if (e.NavigationId != navigationId || e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled) return;
                if (e.IsSuccess) loaded.TrySetResult();
                else loaded.TrySetException(new IOException("Chat composer navigation failed: " + e.WebErrorStatus));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            UseNative(error);
        }
    }

    private void UseNative(Exception error)
    {
        if (_closed) return;
        // A component failure must leave the existing draft editable, without an automatic retry.
        _ready = false; Visibility = Visibility.Collapsed;
        _buffer.Visibility = _send.Visibility = _hint.Visibility = Visibility.Visible;
        OwnedProcessRegistry.Log("chat_composer_failed", "Chat.Composer", detail: error.GetType().Name + ": " + error.Message);
        ComponentAuditFailures.Report(error);
    }

    private void Receive(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_closed) return;
        // Only our immutable local document can navigate this view (NavigationStarting above).
        if (!e.Source.StartsWith("data:text/html;charset=utf-8;base64,", StringComparison.Ordinal)
            && e.Source != "about:blank") return;
        try
        {
            using var data = JsonDocument.Parse(e.WebMessageAsJson);
            var message = data.RootElement;
            if (!message.TryGetProperty("scope", out var scope) || scope.GetString() != DictationScope) return;
            if (message.GetProperty("type").GetString() == "input")
            {
                _receiving = true;
                try { _changed(message.GetProperty("text").GetString() ?? ""); }
                finally { _receiving = false; }
            }
            else if (message.GetProperty("type").GetString() == "send") PostUserAction(scope.GetString()!, _submit);
            else if (message.GetProperty("type").GetString() == "pasteFiles") PostUserAction(scope.GetString()!, () => PasteAttachment?.Invoke());
            else if (message.GetProperty("type").GetString() == "dropFiles")
            {
                var paths = e.AdditionalObjects.OfType<CoreWebView2File>().Select(f => f.Path).ToArray();
                if (paths.Length > 0) PostUserAction(scope.GetString()!, () => FilesDropped?.Invoke(paths));
            }
        }
        catch (JsonException) { }
    }

    private void PostUserAction(string scope, Action action)
    {
        // WebView2 forbids nested modal loops in its callbacks. Sending/pasting can
        // request component terms, so return from WebMessageReceived first.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Normal, new Action(() =>
        {
            if (!_closed && IsVisible && DictationScope == scope) action();
        }));
    }

    internal void SetText(string text)
    {
        if (_ready && !_receiving && !_closed)
        {
            _dictationEpoch++;
            _ = RunScriptAsync(SetScript(text));
        }
    }

    internal void FocusEditor()
    {
        if (_ready && !_closed) { Focus(); _ = RunScriptAsync("composer.focus()"); }
        else _buffer.Focus();
    }

    private void UpdateAppearance(object? sender, EventArgs args)
    {
        if (!_ready || _closed) return;
        if (_documentScope != DictationScope) _ = RunScriptAsync(SetScript(_buffer.Text));
        var color = (_buffer.Foreground as SolidColorBrush)?.Color ?? Colors.White;
        var background = (_buffer.TryFindResource("HeaderBackgroundBrush") as SolidColorBrush)?.Color
            ?? (Window.GetWindow(_buffer)?.Background as SolidColorBrush)?.Color ?? Colors.Black;
        DefaultBackgroundColor = System.Drawing.Color.FromArgb(background.A, background.R, background.G, background.B);
        var state = JsonSerializer.Serialize(new { color = $"#{color.R:X2}{color.G:X2}{color.B:X2}",
            background = $"#{background.R:X2}{background.G:X2}{background.B:X2}",
            label = System.Windows.Automation.AutomationProperties.GetName(_buffer), font = _buffer.FontFamily.Source,
            size = _buffer.FontSize, enabled = _send.IsEnabled, hint = _hint.Text, send = _send.ToolTip as string });
        if (state == _lastTheme) return;
        _lastTheme = state;
        _ = RunScriptAsync("composer.theme(" + state + ")");
    }

    private async Task RunScriptAsync(string script)
    {
        try { if (!_closed) await ExecuteScriptAsync(script); }
        catch (Exception error) when (_closed || error is ObjectDisposedException) { }
        catch (Exception error) { ComponentAuditFailures.Report(error); }
    }

    public new void Dispose()
    {
        if (_closed) return;
        _closed = true; _lifetime.Cancel(); LayoutUpdated -= UpdateAppearance;
        if (CoreWebView2 is { } core) core.WebMessageReceived -= Receive;
        base.Dispose(); _lifetime.Dispose();
    }

    private const string Document = """
        <!doctype html><html><head><meta charset="utf-8">
        <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; script-src 'none'; connect-src 'none'; base-uri 'none'; form-action 'none'">
        <style>
        *{box-sizing:border-box}html,body{margin:0;height:100%;background:transparent;overflow:hidden}
        #scroll{height:calc(100% - 20px);overflow-y:auto;overflow-x:hidden;padding:12px 12px 4px;scrollbar-width:thin;scrollbar-color:#788394 transparent}
        #lines{min-height:100%;white-space:pre-wrap;overflow-wrap:anywhere;line-height:1.32}
        #exclude{float:right;width:56px;height:120px;shape-outside:polygon(0 calc(100% - 36px),100% calc(100% - 36px),100% 100%,0 100%);pointer-events:none}
        #edit{outline:none;min-height:1.32em;white-space:pre-wrap;overflow-wrap:anywhere;caret-color:currentColor}
        #hint{position:absolute;bottom:2px;left:10px;right:60px;font-size:12px;line-height:16px;opacity:.75;pointer-events:none}
        #send{position:absolute;bottom:2px;right:4px;width:44px;height:44px;padding:0;border:0;border-radius:6px;background:#2563eb;color:white;cursor:pointer}
        #send:disabled{opacity:.5;cursor:default}#send:focus-visible{outline:2px solid currentColor;outline-offset:2px}
        #send svg{width:20px;height:20px;vertical-align:middle}
        </style></head><body><div id="scroll"><div id="lines"><i id="exclude" aria-hidden="true"></i><div id="edit" contenteditable="plaintext-only" role="textbox" aria-multiline="true" spellcheck="false"></div></div></div><span id="hint"></span><button id="send" type="button"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 20V4M5 11L12 4L19 11" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"/></svg></button></body></html>
        """;
}
