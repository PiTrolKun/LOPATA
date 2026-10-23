using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using AIHub.Services;
using AIHub.Services.VectorGraphics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AIHub.Controls;

/// <summary>Offline SVG image host. CSP blocks document scripts; only trusted application controls are injected.</summary>
internal sealed class VectorBrowserView : WebView2
{
    // WebView2 environments are COM objects belonging to their creating UI apartment.
    [ThreadStatic] private static Task<CoreWebView2Environment>? _environment;
    private bool _ready, _closed, _updating;
    private double[]? _pending;
    private byte[] _imageBytes = [];
    private int _imageRevision;
    private const string ImageUri = "https://lopata-svg.invalid/image.svg";
    internal double LastUpdateMilliseconds { get; private set; }
    internal event Action<Matrix>? ViewportChanged;
    internal async Task InitializeAsync(string svg, CancellationToken token)
    {
        _ = VectorSvg.Parse(svg);
        using var operation = ComponentAudit.CaptureOperation();
        await ComponentLicenseGate.EnsureAsync(["viewer.webview2", "nuget.Microsoft.Web.WebView2"], token);
        await WebView2ComponentAudit.VerifyAsync(token);
        _environment ??= CoreWebView2Environment.CreateAsync(null, Path.Combine(AppDataPaths.BaseDirectory, "VectorGraphics", "ViewerProfile"));
        CoreWebView2Environment environment;
        try { environment = await _environment; }
        catch { _environment = null; throw; }
        token.ThrowIfCancellationRequested(); if (_closed) throw new OperationCanceledException(token);
        await EnsureCoreWebView2Async(environment);
        var core = CoreWebView2;
        var html = Document(svg);
        var documentUri = "data:text/html;charset=utf-8;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(html));
        _imageBytes = Encoding.UTF8.GetBytes(svg);
        // Enables our injected input callbacks. CSP rejects page scripts; SVG is an inert image and also passes VectorSvg.Parse.
        core.Settings.IsScriptEnabled = true;
        core.Settings.IsWebMessageEnabled = true;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.DownloadStarting += (_, e) => e.Cancel = true;
        ulong navigationId = 0;
        core.NavigationStarting += (_, e) =>
        {
            e.Cancel = e.Uri != documentUri && e.Uri != "about:blank";
            if (!e.Cancel) navigationId = e.NavigationId;
        };
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
        {
            e.Response = e.Request.Uri == ImageUri || e.Request.Uri == ImageUri + "?v=" + _imageRevision
                ? environment.CreateWebResourceResponse(new MemoryStream(_imageBytes, false), 200, "OK", "Content-Type: image/svg+xml; charset=utf-8\r\nCache-Control: no-store")
                : environment.CreateWebResourceResponse(Stream.Null, 403, "Blocked", "Content-Type: text/plain");
        };
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        core.NavigationCompleted += Completed;
        try
        {
            core.NavigateToString(html);
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
            if (_closed) throw new OperationCanceledException(token);
            using var script = typeof(VectorBrowserView).Assembly.GetManifestResourceStream("AIHub.VectorViewerScript")!;
            using var reader = new StreamReader(script);
            await core.ExecuteScriptAsync(await reader.ReadToEndAsync(token));
            core.WebMessageReceived += (_, e) =>
            {
                if (e.Source != core.Source) return;
                try
                {
                    var values = JsonSerializer.Deserialize<double[]>(e.WebMessageAsJson);
                    if (values is not { Length: 3 } || values.Any(x => !double.IsFinite(x)) || values[0] is < .25 or > 32) return;
                    ViewportChanged?.Invoke(new Matrix(values[0], 0, 0, values[0], values[1], values[2]));
                }
                catch (JsonException) { }
            };
            _ready = true;
        }
        finally { core.NavigationCompleted -= Completed; }
        void Completed(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (e.NavigationId != navigationId || e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled) return;
            if (e.IsSuccess) loaded.TrySetResult(); else loaded.TrySetException(new IOException("SVG viewer navigation failed: " + e.WebErrorStatus));
        }
    }
    internal static string Document(string svg)
    {
        _ = VectorSvg.Parse(svg);
        return "<!doctype html><html><head><meta charset='utf-8'><meta http-equiv='Content-Security-Policy' content=\"default-src 'none'; img-src https://lopata-svg.invalid; style-src 'unsafe-inline'; script-src 'none'; connect-src 'none'; base-uri 'none'; form-action 'none'\">"
            + "<style>html,body{width:100%;height:100%;margin:0;overflow:hidden;background:repeating-conic-gradient(#ddd 0 25%,#f7f7f7 0 50%) 0/20px 20px}"
            + "img{position:absolute;max-width:none;user-select:none;pointer-events:none}</style></head><body><img id='vector' draggable='false' src='"
            + ImageUri + "'></body></html>";
    }
    internal async Task SetViewportAsync(double width, double height, double aspect, Matrix matrix)
    {
        if (!_ready || _closed || width <= 0 || height <= 0) return;
        _pending = [width, height, aspect, matrix.M11, matrix.OffsetX, matrix.OffsetY];
        if (_updating) return;
        _updating = true;
        try
        {
            while (_pending is { } values && !_closed)
            {
                _pending = null; var timer = Stopwatch.StartNew();
                await CoreWebView2.ExecuteScriptAsync("window.__lopataVector.set(" + JsonSerializer.Serialize(values) + ")");
                LastUpdateMilliseconds = timer.Elapsed.TotalMilliseconds;
            }
        }
        finally { _updating = false; }
    }
    internal async Task ReplaceSvgAsync(string svg, CancellationToken token)
    {
        _ = VectorSvg.Parse(svg);
        token.ThrowIfCancellationRequested();
        if (!_ready || _closed) return;
        _imageBytes = Encoding.UTF8.GetBytes(svg);
        var uri = ImageUri + "?v=" + ++_imageRevision;
        // Only a trusted resource URL enters JavaScript. SVG/XML is never injected into the document DOM.
        await CoreWebView2.ExecuteScriptAsync("document.getElementById('vector').src=" + JsonSerializer.Serialize(uri));
    }
    internal void CloseView() { if (_closed) return; _closed = true; _ready = false; _pending = null; Dispose(); }
}
