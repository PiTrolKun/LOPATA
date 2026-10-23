using System.IO;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.VectorGraphics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AIHub.Controls.VectorEditor;

/// <summary>Editable offline DOM, isolated from the inert public viewer. Only validated SVG and trusted app scripts enter it.</summary>
internal sealed class VectorEditorBrowser : WebView2
{
    [ThreadStatic] private static Task<CoreWebView2Environment>? _environment;
    private bool _ready, _closed;
    internal event Action<JsonElement>? Message;
    internal async Task InitializeAsync()
    {
        using var audit = ComponentAudit.CaptureOperation();
        await ComponentLicenseGate.EnsureAsync(["viewer.webview2", "nuget.Microsoft.Web.WebView2"], CancellationToken.None);
        await WebView2ComponentAudit.VerifyAsync(CancellationToken.None);
        _environment ??= CoreWebView2Environment.CreateAsync(null, Path.Combine(AppDataPaths.BaseDirectory, "VectorGraphics", "EditorProfile"));
        CoreWebView2Environment environment;
        try { environment = await _environment; } catch { _environment = null; throw; }
        if (_closed) return;
        await EnsureCoreWebView2Async(environment);
        var core = CoreWebView2;
        core.Settings.AreHostObjectsAllowed = false; core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false; core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsZoomControlEnabled = false; core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false; core.Settings.IsPasswordAutosaveEnabled = false;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.NewWindowRequested += (_, e) => e.Handled = true; core.DownloadStarting += (_, e) => e.Cancel = true;
        var loading = true;
        core.NavigationStarting += (_, e) => e.Cancel = !loading || !(e.Uri == "about:blank" || e.Uri.StartsWith("data:text/html;", StringComparison.Ordinal));
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) => e.Response = environment.CreateWebResourceResponse(Stream.Null, 403, "Blocked", "");
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Completed(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        { if (e.IsSuccess) loaded.TrySetResult(); else loaded.TrySetException(new IOException(e.WebErrorStatus.ToString())); }
        core.NavigationCompleted += Completed;
        try
        {
            core.NavigateToString(Document);
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(20)); loading = false;
            if (_closed) return;
            foreach (var name in new[] { "AIHub.VectorEditorScript", "AIHub.VectorEditorCommands", "AIHub.VectorEditorPlastic" })
            {
                using var source = typeof(VectorEditorBrowser).Assembly.GetManifestResourceStream(name)!;
                using var reader = new StreamReader(source);
                await core.ExecuteScriptAsync(await reader.ReadToEndAsync());
            }
            core.WebMessageReceived += (_, e) =>
            {
                if (_closed || e.Source != core.Source || e.WebMessageAsJson.Length > 48 * 1024 * 1024) return;
                using var json = JsonDocument.Parse(e.WebMessageAsJson);
                Message?.Invoke(json.RootElement.Clone());
            };
            _ready = true;
        }
        finally { core.NavigationCompleted -= Completed; }
    }
    internal async Task LoadAsync(string svg, string sheet, double zoom, double panX, double panY, bool fit = false)
    {
        _ = VectorSvg.Parse(svg);
        await CallAsync("load", new { svg, sheet, zoom, panX, panY, fit });
    }
    internal async Task<JsonElement> CallAsync(string method, object? argument = null)
    {
        if (!_ready || _closed) return default;
        if (!new[] { "load", "command", "select", "tool", "fit", "get", "plastic", "theme", "drawing" }.Contains(method)) throw new ArgumentOutOfRangeException(nameof(method));
        var result = await CoreWebView2.ExecuteScriptAsync("window.lopataEditor." + method + "(" + JsonSerializer.Serialize(argument) + ")");
        return JsonSerializer.Deserialize<JsonElement>(result);
    }
    internal void CloseView() { if (_closed) return; _closed = true; _ready = false; Dispose(); }
    internal const string Document = """
        <!doctype html><html><head><meta charset="utf-8">
        <meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'none'; style-src 'unsafe-inline'; img-src 'none'; connect-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'">
        <style>
        html,body{margin:0;width:100%;height:100%;overflow:hidden;background:#101923;color:#aab4c4;user-select:none}
        #stage{position:absolute;inset:0;touch-action:none;outline:none;background-image:radial-gradient(#50607066 .7px,transparent .7px);background-size:20px 20px}
        #paper{position:absolute;transform-origin:0 0;box-shadow:0 4px 28px #0008;background:repeating-conic-gradient(#d4d8dc 0 25%,#eef0f2 0 50%) 0/20px 20px}
        #paper>svg{display:block;width:100%;height:100%;overflow:visible}
        #overlay{position:absolute;inset:0;width:100%;height:100%;overflow:visible;pointer-events:none}
        .box{fill:none;stroke:#31dfad;stroke-width:1;stroke-dasharray:5 3}
        .handle{fill:#fff;stroke:#13ac81;stroke-width:1;pointer-events:all;cursor:nwse-resize}
        .node{fill:#fff;stroke:#007bff;stroke-width:1;pointer-events:all;cursor:move}
        </style></head><body><div id="stage" tabindex="0"><div id="paper"></div><svg id="overlay"></svg></div></body></html>
        """;
}
