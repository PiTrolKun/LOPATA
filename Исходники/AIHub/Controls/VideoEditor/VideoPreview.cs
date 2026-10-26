using System.IO;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.VideoEditor;
using AIHub.Services.VectorGraphics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AIHub.Controls.VideoEditor;

internal sealed class VideoPreview : WebView2
{
    private const string Origin = "https://lopata-video.invalid/";
    private readonly Dictionary<string, (string Path, string Kind)> _files = [];
    private readonly HashSet<Stream> _streams = [];
    private CoreWebView2Environment? _environment;
    public bool Ready { get; private set; }
    public event Action<double>? PositionChanged;
    public event Action<string, double, double, double, double>? FramingChanged;
    public event Action<string>? FramingSelected;
    public event Action<bool>? FramingGesture;
    internal string ResizeLabel { get; set; } = "Resize";
    internal string RotateLabel { get; set; } = "Rotate";
    public event Action<bool>? ExpandedRequested;
    public async Task InitializeAsync(CancellationToken token)
    {
        using var audit = ComponentAudit.CaptureOperation();
        await ComponentLicenseGate.EnsureAsync(["viewer.webview2", "nuget.Microsoft.Web.WebView2"], token); await WebView2ComponentAudit.VerifyAsync(token);
        _environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(VideoProjectStore.Root, "Browser"), new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required" });
        await EnsureCoreWebView2Async(_environment);
        var core = CoreWebView2;
        core.Settings.AreHostObjectsAllowed = false; core.Settings.AreDevToolsEnabled = false; core.Settings.AreDefaultContextMenusEnabled = false; core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false; core.Settings.IsGeneralAutofillEnabled = false;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.NewWindowRequested += (_, e) => e.Handled = true; core.DownloadStarting += (_, e) => e.Cancel = true;
        core.NavigationStarting += (_, e) => e.Cancel = !e.Uri.StartsWith("data:text/html", StringComparison.Ordinal) && e.Uri != "about:blank";
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += Resource;
        core.WebMessageReceived += (_, e) => {
            if (e.Source != core.Source) return;
            try {
                using var message = JsonDocument.Parse(e.WebMessageAsJson); var value = message.RootElement;
                if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var at) && double.IsFinite(at) && at >= 0) PositionChanged?.Invoke(at);
                else if (value.ValueKind == JsonValueKind.Object && value.GetProperty("kind").GetString() == "expanded" && value.GetProperty("value").ValueKind is JsonValueKind.True or JsonValueKind.False) ExpandedRequested?.Invoke(value.GetProperty("value").GetBoolean());
                else if (value.ValueKind == JsonValueKind.Object && value.GetProperty("kind").GetString() == "framingSelect") {
                    var id = value.GetProperty("clipId").GetString(); if (Guid.TryParseExact(id, "N", out var selectedGuid)) FramingSelected?.Invoke(id!);
                }
                else if (value.ValueKind == JsonValueKind.Object && value.GetProperty("kind").GetString() == "framingGesture" && value.GetProperty("value").ValueKind is JsonValueKind.True or JsonValueKind.False) FramingGesture?.Invoke(value.GetProperty("value").GetBoolean());
                else if (value.ValueKind == JsonValueKind.Object && value.GetProperty("kind").GetString() == "framing") {
                    var id = value.GetProperty("clipId").GetString(); var x = value.GetProperty("x").GetDouble(); var y = value.GetProperty("y").GetDouble(); var scale = value.GetProperty("scale").GetDouble(); var rotation = value.GetProperty("rotation").GetDouble();
                    if (Guid.TryParseExact(id, "N", out var framedGuid) && double.IsFinite(x) && double.IsFinite(y) && Math.Abs(x) <= 1000000 && Math.Abs(y) <= 1000000 && scale is >= .01 and <= 20 && double.IsFinite(rotation)) FramingChanged?.Invoke(id!, x, y, scale, rotation);
                }
            } catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException) { }
        };
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        core.NavigationCompleted += Complete;
        core.NavigateToString("<!doctype html><html><head><meta charset='utf-8'><meta http-equiv='Content-Security-Policy' content=\"default-src 'none'; img-src https://lopata-video.invalid; media-src https://lopata-video.invalid; style-src 'unsafe-inline'; script-src 'none'; connect-src 'none'; base-uri 'none'; form-action 'none'\"><style>html,body{margin:0;width:100%;height:100%;overflow:hidden;background:#050912}#stage{position:absolute;overflow:hidden;background:#000;transform-origin:0 0}#stage>div{position:absolute;inset:0;display:flex;align-items:center;justify-content:center}img,video{max-width:100%;max-height:100%;object-fit:contain;transform-origin:center}#sub{position:absolute;bottom:6%;left:5%;width:90%;color:white;text-align:center;font:28px 'Segoe UI';text-shadow:0 1px 4px black;background:#0008;white-space:pre-wrap;pointer-events:none}#sub:empty{display:none}</style></head><body><div id='stage'></div></body></html>");
        try { await loaded.Task.WaitAsync(TimeSpan.FromSeconds(20), token); }
        finally { core.NavigationCompleted -= Complete; }
        foreach (var name in new[] { "AIHub.VideoEditorFramingScript", "AIHub.VideoEditorPreviewScript" }) {
            using var source = typeof(VideoPreview).Assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(source); await core.ExecuteScriptAsync(await reader.ReadToEndAsync(token));
        }
        Ready = true;
        void Complete(object? sender, CoreWebView2NavigationCompletedEventArgs e) { if (e.IsSuccess) loaded.TrySetResult(); else loaded.TrySetException(new IOException("VideoEditor.PreviewFailed")); }
    }
    public async Task LoadAsync(VideoProject project, double at, bool subtitles, string? subtitleTrack = null, string? framingClip = null, bool framingEnabled = false)
    {
        if (!Ready) return; _files.Clear();
        var urls = new Dictionary<string, string>();
        var audioUrls = new Dictionary<string, string>();
        foreach (var a in project.Assets)
        {
            var path = a.Kind is "video" or "audio" ? a.Proxy : a.Path; if (path is null || !File.Exists(path)) continue;
            var uri = Origin + a.Id; _files[uri] = (path, a.Kind); urls[a.Id] = uri;
            if (a.Kind == "video" && a.AudioStream >= 0 && File.Exists(VideoPreviewAssets.AudioPath(project, a))) {
                var audioUri = uri + "/audio"; _files[audioUri] = (VideoPreviewAssets.AudioPath(project, a), "audio"); audioUrls[a.Id] = audioUri;
            }
        }
        var view = project.Clone(); view.Narration = VideoSubtitles.Cues(project).Where(n => subtitleTrack is null || n.SubtitleTrackId + "|" + n.Language == subtitleTrack).ToList();
        await CoreWebView2.ExecuteScriptAsync("window.lopata.load(" + JsonSerializer.Serialize(new { project = view, urls, audioUrls, at, subtitles, framingClip, framingEnabled, framingLabels = new { resize = ResizeLabel, rotate = RotateLabel } }) + ")");
    }
    public Task SetFramingAsync(string? id, bool enabled = true) => Ready ? CoreWebView2.ExecuteScriptAsync("window.lopata.framing(" + JsonSerializer.Serialize(id) + "," + (enabled ? "true" : "false") + ")") : Task.CompletedTask;
    public Task SetExpandedAsync(bool expanded, string title) => Ready ? CoreWebView2.ExecuteScriptAsync("window.lopata.expanded(" + JsonSerializer.Serialize(new { expanded, title }) + ")") : Task.CompletedTask;
    public Task SeekAsync(double at) => Ready ? CoreWebView2.ExecuteScriptAsync("window.lopata.seek(" + VideoExport.N(at) + ")") : Task.CompletedTask;
    public Task PlayAsync(bool play) => Ready ? CoreWebView2.ExecuteScriptAsync("window.lopata.play(" + (play ? "true" : "false") + ")") : Task.CompletedTask;
    public Task SetVolumesAsync(VideoProject project) => Ready ? CoreWebView2.ExecuteScriptAsync("window.lopata.volumes(" + JsonSerializer.Serialize(new {
        tracks = project.Tracks.Where(t => t.Kind == "audio").ToDictionary(t => t.Id, t => t.Volume),
        clips = project.Tracks.Where(t => t.Kind == "audio").SelectMany(t => t.Clips).ToDictionary(c => c.Id, c => c.Volume)
    }) + ")") : Task.CompletedTask;
    private void Resource(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        try { Serve(e); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        { e.Response = _environment!.CreateWebResourceResponse(Stream.Null, 404, "Unavailable", ""); OwnedProcessRegistry.Log("video_preview_asset_failed", "VideoEditor", detail: error.GetType().Name); }
    }
    private void Serve(CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (!_files.TryGetValue(e.Request.Uri, out var file)) { e.Response = _environment!.CreateWebResourceResponse(Stream.Null, 403, "Blocked", ""); return; }
        if (file.Kind == "svg")
        {
            var svg = File.ReadAllText(file.Path); _ = VectorSvg.Parse(svg);
            e.Response = _environment!.CreateWebResourceResponse(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(svg)), 200, "OK", "Content-Type: image/svg+xml\r\nCache-Control: no-store\r\nAccess-Control-Allow-Origin: *"); return;
        }
        var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        long start = 0, end = stream.Length - 1; var partial = false;
        if (e.Request.Headers.Contains("Range"))
        {
            var parts = e.Request.Headers.GetHeader("Range").Replace("bytes=", "", StringComparison.Ordinal).Split('-');
            if (parts.Length == 2 && long.TryParse(parts[0], out start) && start >= 0 && start <= end) { if (long.TryParse(parts[1], out var stop)) end = Math.Min(end, stop); partial = end >= start; }
            if (!partial) { stream.Dispose(); e.Response = _environment!.CreateWebResourceResponse(Stream.Null, 416, "Range Not Satisfiable", ""); return; }
        }
        var total = stream.Length; stream.Position = start; var bounded = new SliceStream(stream, end - start + 1); _streams.Add(bounded); _streams.RemoveWhere(s => !s.CanRead);
        var type = Path.GetExtension(file.Path).ToLowerInvariant() switch { ".mp4" => "video/mp4", ".m4a" => "audio/mp4", ".wav" => "audio/wav", ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", _ => "application/octet-stream" };
        var headers = $"Content-Type: {type}\r\nAccept-Ranges: bytes\r\nContent-Length: {end - start + 1}\r\nCache-Control: no-store\r\nAccess-Control-Allow-Origin: *" + (partial ? $"\r\nContent-Range: bytes {start}-{end}/{total}" : "");
        e.Response = _environment!.CreateWebResourceResponse(bounded, partial ? 206 : 200, partial ? "Partial Content" : "OK", headers);
    }
    protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) { foreach (var s in _streams) s.Dispose(); _streams.Clear(); } }
    private sealed class SliceStream(FileStream source, long length) : Stream
    {
        private long _position;
        public override bool CanRead => source.CanRead; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => length; public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { var n = source.Read(buffer, offset, (int)Math.Min(count, length - _position)); _position += n; return n; }
        protected override void Dispose(bool disposing) { if (disposing) source.Dispose(); base.Dispose(disposing); }
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
