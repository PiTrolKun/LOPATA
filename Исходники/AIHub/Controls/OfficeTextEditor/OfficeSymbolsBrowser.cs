using System.IO;
using System.Text.Json;
using System.Text;
using System.Windows.Media;
using AIHub.Services;
using AIHub.Services.OfficeEditor;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Color = System.Windows.Media.Color;

namespace AIHub.Controls.OfficeTextEditor;

/// <summary>One local, virtualized surface. Chromium shapes and paints the installed color fonts.</summary>
internal sealed class OfficeSymbolsBrowser : WebView2
{
    private const string Entry = "https://office-symbols.lopata.invalid/index.html";
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private OfficeUnicodeSymbol[] _items = [];
    private int _revision;
    private bool _closed;
    internal int EntryCount => _items.Length;
    internal event Action<OfficeUnicodeSymbol>? Chosen;
    internal event Action<Exception>? Failed;

    internal async Task InitializeAsync(CancellationToken token)
    {
        using var audit = ComponentAudit.CaptureOperation();
        await ComponentLicenseGate.EnsureAsync(["viewer.webview2", "nuget.Microsoft.Web.WebView2"], token);
        await WebView2ComponentAudit.VerifyAsync(token);
        var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(AppDataPaths.BaseDirectory, "OfficeEditor", "BrowserProfile"));
        token.ThrowIfCancellationRequested(); await EnsureCoreWebView2Async(environment); token.ThrowIfCancellationRequested();
        var core = CoreWebView2;
        core.Settings.AreHostObjectsAllowed = false; core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false; core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsStatusBarEnabled = false; core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false; core.Settings.AreDefaultContextMenusEnabled = false;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.NavigationStarting += (_, e) => e.Cancel = _ready.Task.IsCompleted || e.Uri != Entry;
        core.FrameNavigationStarting += (_, e) => e.Cancel = true;
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) => e.Response = e.Request.Uri == Entry
            ? environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(OfficeSymbolsBrowserPage.Html)), 200, "OK", "Content-Type: text/html; charset=utf-8")
            : environment.CreateWebResourceResponse(Stream.Null, 403, "Blocked", "");
        core.ProcessFailed += (_, e) =>
        {
            if (e.ProcessFailedKind is not (CoreWebView2ProcessFailedKind.BrowserProcessExited or CoreWebView2ProcessFailedKind.RenderProcessExited)) return;
            var error = new IOException("OfficeSymbols.DataMissing"); _ready.TrySetException(error); if (!_closed) Failed?.Invoke(error);
        };
        core.NavigationCompleted += (_, e) => { if (!e.IsSuccess) _ready.TrySetException(new IOException(e.WebErrorStatus.ToString())); };
        core.WebMessageReceived += Receive;
        core.Navigate(Entry);
        await _ready.Task.WaitAsync(TimeSpan.FromSeconds(40), token);
    }

    internal void SetItems(OfficeUnicodeSymbol[] items)
    {
        if (_closed) return;
        _items = items; _revision++;
        Send(new { type = "library", revision = _revision, count = items.Length });
    }

    internal void SetStyle(string[] fonts, Color foreground, Color background, Color line, string selectedLabel, string language)
    {
        static string Css(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        Send(new { type = "style", fonts, foreground = Css(foreground), background = Css(background), line = Css(line), selectedLabel, language });
    }
    internal void SetSelection(string text) => Send(new { type = "selection", text });
    private void Send(object message)
    { if (!_closed && CoreWebView2 is { } core) core.PostWebMessageAsJson(JsonSerializer.Serialize(message)); }

    private void Receive(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_closed || e.Source != Entry) return;
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson); var message = document.RootElement;
            var type = message.GetProperty("type").GetString();
            if (type == "ready") { _ready.TrySetResult(); return; }
            if (message.GetProperty("revision").GetInt32() != _revision) return;
            var start = message.GetProperty("start").GetInt32();
            if (start < 0 || start >= _items.Length) return;
            if (type == "choose") { Chosen?.Invoke(_items[start]); return; }
            if (type != "range") return;
            var length = Math.Clamp(message.GetProperty("count").GetInt32(), 1, 256);
            Send(new { type = "rows", revision = _revision, start, items = _items.Skip(start).Take(length)
                .Select(s => new { text = s.Preview, code = s.Code, hint = s.Hint }) });
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException or KeyNotFoundException)
        { Failed?.Invoke(error); }
    }

    internal void CloseBrowser()
    {
        if (_closed) return; _closed = true; _items = []; _ready.TrySetCanceled();
        if (CoreWebView2 is { } core) core.WebMessageReceived -= Receive;
        Dispose();
    }
}
