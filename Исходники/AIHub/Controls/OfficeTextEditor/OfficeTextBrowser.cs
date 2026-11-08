using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHub.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AIHub.Controls.OfficeTextEditor;

/// <summary>Bridges WPF to the packaged editor. Navigation, file pickers and all external resources stay under host control.</summary>
internal sealed class OfficeTextBrowser : WebView2
{
    private const string Site = "https://office-editor.lopata.invalid";
    private const string Entry = Site + "/index.html";
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<long, TaskCompletionSource<JsonElement>> _requests = [];
    private long _id;
    private bool _closed;
    internal event Action<JsonElement>? Message;
    internal event Action<Exception>? SurfaceFailed;

    internal async Task InitializeSurfaceAsync(CancellationToken token)
    {
        using var audit = ComponentAudit.CaptureOperation();
        await ComponentLicenseGate.EnsureAsync(["office.text-surface", "viewer.webview2", "nuget.Microsoft.Web.WebView2"], token);
        await WebView2ComponentAudit.VerifyAsync(token);
        var directory = Path.Combine(AppContext.BaseDirectory, "Tools", "OfficeTextSurface", "public");
        await using var manifest = typeof(OfficeTextBrowser).Assembly.GetManifestResourceStream("AIHub.OfficeTextSurfaceManifest")
            ?? throw new InvalidDataException("OfficeEditor.EngineUnavailable");
        using var json = await JsonDocument.ParseAsync(manifest, cancellationToken: token);
        var inventory = json.RootElement.GetProperty("Files").EnumerateArray().Select(f =>
            new PinnedTreeFile(f.GetProperty("Name").GetString()!, f.GetProperty("Bytes").GetInt64(), f.GetProperty("Sha256").GetString()!)).ToArray();
        await RuntimeFileAudit.VerifyAsync("office.text-surface", directory, json.RootElement.GetProperty("Version").GetString()!, inventory, token);
        var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(AppDataPaths.BaseDirectory, "OfficeEditor", "BrowserProfile"));
        token.ThrowIfCancellationRequested();
        await EnsureCoreWebView2Async(environment);
        token.ThrowIfCancellationRequested();
        var core = CoreWebView2;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsBuiltInErrorPageEnabled = false;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.DownloadStarting += (_, e) => e.Cancel = true;
        var allowEntry = true;
        core.NavigationStarting += (_, e) =>
        {
            e.Cancel = !allowEntry || e.Uri != Entry;
            if (!e.Cancel) allowEntry = false;
        };
        core.FrameNavigationStarting += (_, e) => e.Cancel = true;
        core.SetVirtualHostNameToFolderMapping(new Uri(Site).Host, directory, CoreWebView2HostResourceAccessKind.DenyCors);
        var files = inventory.Select(f => "/" + f.Path.Replace('\\', '/')).ToHashSet(StringComparer.Ordinal);
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
        {
            var uri = new Uri(e.Request.Uri);
            if (uri.Scheme is "data" or "blob") return;
            if (uri.GetLeftPart(UriPartial.Authority) == Site && files.Contains(Uri.UnescapeDataString(uri.AbsolutePath))) return;
            e.Response = environment.CreateWebResourceResponse(Stream.Null, 403, "Blocked", "");
        };
        core.NavigationCompleted += (_, e) =>
        {
            if (!e.IsSuccess) _ready.TrySetException(new IOException(e.WebErrorStatus.ToString()));
        };
        core.ProcessFailed += (_, e) =>
        {
            if (e.ProcessFailedKind is not (CoreWebView2ProcessFailedKind.BrowserProcessExited or CoreWebView2ProcessFailedKind.RenderProcessExited)) return;
            var error = new IOException("OfficeEditor.EngineUnavailable");
            _ready.TrySetException(error);
            foreach (var request in _requests.Values) request.TrySetException(error);
            SurfaceFailed?.Invoke(error);
        };
        core.WebMessageReceived += Receive;
        core.Navigate(Entry);
        await _ready.Task.WaitAsync(TimeSpan.FromSeconds(60), token);
    }

    private void Receive(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_closed || e.Source != Entry) return;
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var msg = document.RootElement;
            if (msg.ValueKind != JsonValueKind.Object) return;
            if (msg.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var number)
                && _requests.TryGetValue(number, out var pending))
            {
                if (msg.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
                    pending.TrySetResult(msg.TryGetProperty("result", out var result) ? result.Clone() : default);
                else
                    pending.TrySetException(new InvalidOperationException(msg.TryGetProperty("error", out var error) ? error.ToString() : "OfficeEditor.Failed"));
                return;
            }
            if (!msg.TryGetProperty("type", out var type)) return;
            if (type.GetString() == "ready") _ready.TrySetResult();
            else Message?.Invoke(msg.Clone());
        }
        catch (JsonException error) { SurfaceFailed?.Invoke(error); }
    }

    internal async Task<JsonElement> RequestAsync(object command, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        token.ThrowIfCancellationRequested();
        var id = ++_id;
        var request = JsonSerializer.SerializeToNode(command)!.AsObject();
        request["id"] = id;
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _requests.Add(id, completion);
        try
        {
            CoreWebView2.PostWebMessageAsJson(request.ToJsonString());
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(90), token);
        }
        finally { _requests.Remove(id); }
    }

    internal void CloseSurface()
    {
        if (_closed) return;
        _closed = true;
        _ready.TrySetCanceled();
        foreach (var item in _requests.Values) item.TrySetCanceled();
        if (CoreWebView2 is { } core) core.WebMessageReceived -= Receive;
        Dispose();
    }
}
