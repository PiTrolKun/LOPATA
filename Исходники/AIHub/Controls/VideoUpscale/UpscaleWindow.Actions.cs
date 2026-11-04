using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VideoEditor;
using AIHub.Services.VideoUpscale;
using Panel = System.Windows.Controls.Panel;

namespace AIHub.Controls.VideoUpscale;

internal sealed partial class UpscaleWindow
{
    private readonly SemaphoreSlim _proxyGate = new(1);
    private long _proxyRevision;
    internal async Task RestoreProject(UpscaleProject project)
    {
        if (_busy) throw new InvalidOperationException("VideoUpscale.WindowBusy");
        _project = project; NormalizeProject(); RefreshSteps(); if (File.Exists(_project.Source)) await RefreshMedia();
    }
    private async Task Import()
    {
        if (_busy) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = T("VideoFilter") };
        if (dialog.ShowDialog(this) != true) return;
        SetBusy(true);
        try {
            _status.Text = T("Importing");
            var media = await UpscaleMediaTools.Describe(dialog.FileName, _lifetime.Token);
            var fresh = new UpscaleProject { Name = Path.GetFileNameWithoutExtension(dialog.FileName), Source = Path.GetFullPath(dialog.FileName), Media = media,
                SourceLength = new FileInfo(dialog.FileName).Length, SourceSha256 = await UpscaleMediaTools.Hash(dialog.FileName, _lifetime.Token), Mode = _project.Mode,
                Steps = UpscaleStore.Clone(_project.Steps), PreviewLength = Math.Min(5, media.Duration) };
            fresh.Workspace = UpscaleStore.Work(fresh.Id); _project = fresh; Save(); await RefreshMedia();
        } finally { SetBusy(false); }
    }
    private async Task OpenProject()
    {
        if (_busy) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = T("ProjectFilter"), InitialDirectory = Path.Combine(UpscaleStore.Root, "Projects") };
        if (dialog.ShowDialog(this) != true) return;
        var next = UpscaleStore.Load<UpscaleProject>(dialog.FileName); UpscaleCatalog.Validate(next, allowEmptyChain: true);
        _project = next; NormalizeProject(); RefreshSteps(); await RefreshMedia(); Save();
    }
    private void SaveAs()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = T("ProjectFilter"), DefaultExt = ".lopataUpscale", FileName = _project.Name };
        if (dialog.ShowDialog(this) == true) { Save(); UpscaleStore.Save(dialog.FileName, _project); }
    }
    private async Task RefreshMedia()
    {
        if (!File.Exists(_project.Source)) throw new FileNotFoundException(T("SourceMissing"));
        Directory.CreateDirectory(_project.Workspace);
        _sourceText.Text = $"{_project.Name} · {_project.Media.Width} × {_project.Media.Height} · {_project.Media.Fps:0.###} fps · {TimeSpan.FromSeconds(_project.Media.Duration)}";
        _loading = true; _rangeStart.Maximum = Math.Max(0, _project.Media.Duration - _project.PreviewLength);
        _rangeStart.Value = _project.PreviewStart; _rangeLength.Maximum = Math.Min(5, _project.Media.Duration); _rangeLength.Value = _project.PreviewLength; _position.Maximum = _project.PreviewLength; _loading = false;
        UpdateRangeText(); await OriginalProxy(); await Filmstrip();
        if (_project.PreviewPath is {} preview && File.Exists(preview) && _project.PreviewFingerprint == PreviewFingerprint()) await ResultProxy(preview);
    }
    private void RangeChanged()
    {
        if (_loading || _busy || string.IsNullOrWhiteSpace(_project.Source)) return;
        _project.PreviewLength = Math.Clamp(_rangeLength.Value, Math.Min(.1, _project.Media.Duration), Math.Min(5, _project.Media.Duration));
        _project.PreviewStart = Math.Min(_rangeStart.Value, _project.Media.Duration - _project.PreviewLength);
        _loading = true; _rangeStart.Maximum = Math.Max(0, _project.Media.Duration - _project.PreviewLength); _rangeStart.Value = _project.PreviewStart; _position.Maximum = _project.PreviewLength; _loading = false;
        Interlocked.Increment(ref _proxyRevision); UpdateRangeText(); Save(); _rangeRefresh.Stop(); _rangeRefresh.Start();
    }
    private void UpdateRangeText() { _rangeText.Text = T("PreviewRange") + $" {_project.PreviewStart:0.00}–{_project.PreviewStart + _project.PreviewLength:0.00} s · " + T("MaxFive"); _range.Set(_project.Media.Duration, _project.PreviewStart, _project.PreviewLength); }
    private string PreviewFingerprint() => UpscaleCatalog.Fingerprint(_project) + "|" + UpscaleMediaTools.N(_project.PreviewStart) + "|" + UpscaleMediaTools.N(_project.PreviewLength);
    private async Task OriginalProxy()
    {
        var revision = Interlocked.Increment(ref _proxyRevision); var project = _project;
        var start = project.PreviewStart; var length = project.PreviewLength;
        await _proxyGate.WaitAsync(_lifetime.Token);
        try {
        if (revision != _proxyRevision || project != _project) return;
        await _original.PlayAsync(false); await _result.PlayAsync(false); _playing = false;
        var proxy = Path.Combine(project.Workspace, "original-preview-" + Guid.NewGuid().ToString("N") + ".mp4");
        // Release the browser stream before replacing its previous proxy.
        await _original.LoadAsync(new VideoProject(), 0, false);
        await UpscaleMediaTools.MakeProxy(project.Source, proxy, start, length, _lifetime.Token);
        if (revision != _proxyRevision || project != _project) return;
        _sourceProxy = proxy; await LoadPlayer(_original, proxy, true); await Seek(0); await ApplyView();
        } finally { _proxyGate.Release(); }
    }
    private async Task ResultProxy(string path)
    {
        var proxy = path;
        if (_project.Codec != "libopenh264" || !Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase)) {
            proxy = Path.Combine(_project.Workspace, "result-preview-" + Guid.NewGuid().ToString("N") + ".mp4");
            await UpscaleMediaTools.MakeProxy(path, proxy, 0, _project.PreviewLength, _lifetime.Token);
        }
        await LoadPlayer(_result, proxy, false); await Seek(0); await ApplyView();
    }
    private async Task LoadPlayer(VideoEditor.VideoPreview view, string path, bool sound)
    {
        var media = await UpscaleMediaTools.Describe(path, _lifetime.Token);
        var asset = new VideoAsset { Path = path, Proxy = path, Name = Path.GetFileName(path), Duration = _project.PreviewLength, Width = media.Width, Height = media.Height, Kind = "video", AudioStream = sound ? 0 : -1 };
        var p = new VideoProject { Width = media.Width, Height = media.Height, Fps = _project.Media.Fps, Assets = [asset] };
        p.Tracks[0].Clips.Add(new() { AssetId = asset.Id, Duration = _project.PreviewLength, Volume = sound ? 1 : 0 });
        await view.LoadAsync(p, 0, false);
    }
    private async Task Seek(double at)
    {
        _seeking = true;
        try { _at = at; await _original.SeekAsync(at); await _result.SeekAsync(at); Clock(); }
        finally { _seeking = false; }
    }
    private void Clock() { _clock.Text = TimeSpan.FromSeconds(_at).ToString(@"hh\:mm\:ss\.fff") + " / " + TimeSpan.FromSeconds(_project.PreviewLength).ToString(@"hh\:mm\:ss"); _range.Position = _project.PreviewStart + _at; }
    private async Task Filmstrip()
    {
        _range.Thumbnails.Clear(); _range.ClipName = _project.Name; var directory = Path.Combine(_project.Workspace, "Filmstrip"); Directory.CreateDirectory(directory);
        for (var i = 0; i < 10; i++) {
            var at = _project.Media.Duration * i / 10; var path = Path.Combine(directory, i + ".png");
            if (!File.Exists(path)) await UpscaleFfmpeg.RunAsync(false, ["-v", "error", "-y", "-ss", UpscaleMediaTools.N(at), "-i", _project.Source, "-frames:v", "1", "-vf", "scale=120:68:force_original_aspect_ratio=decrease", path], _lifetime.Token);
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.UriSource = new Uri(path); bitmap.EndInit(); bitmap.Freeze();
            _range.Thumbnails.Add(bitmap); _range.InvalidateVisual();
        }
    }
    private void SetBusy(bool busy)
    {
        _busy = busy; _toolbar.IsEnabled = !busy; _software.IsEnabled = !busy; _ai.IsEnabled = !busy; _aiCommands.IsEnabled = !busy; _range.IsEnabled = !busy; _rangeStart.IsEnabled = !busy; _rangeLength.IsEnabled = !busy;
    }
    private async Task Process(bool preview)
    {
        if (_busy || !File.Exists(_project.Source)) return;
        UpscaleCatalog.Validate(_project); Save();
        string output;
        if (preview) output = Path.Combine(_project.Workspace, "Previews", Guid.NewGuid().ToString("N") + (_project.Codec == "ffv1" ? ".mkv" : ".mp4"));
        else {
            var dialog = new Microsoft.Win32.SaveFileDialog { Filter = _project.Codec == "ffv1" ? "Matroska (*.mkv)|*.mkv" : "MP4 (*.mp4)|*.mp4|Matroska (*.mkv)|*.mkv", FileName = _project.Name + "-4K", DefaultExt = _project.Codec == "ffv1" ? ".mkv" : ".mp4" };
            if (dialog.ShowDialog(this) != true) return; output = dialog.FileName;
        }
        SetBusy(true);
        _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, ApplicationBackgroundOperations.ExitToken);
        try {
            if (EnsureRuntime is not null) await EnsureRuntime();
            var models = PrepareModels is null ? "" : await PrepareModels(_project, _operation.Token);
            if (models is null) return;
            if (preview) await OriginalProxy();
            var job = UpscalePipeline.Create(_project, preview, output);
            await ExecuteJob(job, models, _operation.Token);
        } finally { _operation.Dispose(); _operation = null; SetBusy(false); }
    }
    internal async Task ExecuteJob(UpscaleJob job, string models, CancellationToken token, BackgroundOperationState? restored = null)
    {
        // Resume always presents the frozen job settings, even if the same project was
        // edited after stopping. Never mark an old-chain preview as a new-chain result.
        if (_project.Id != job.Project.Id || UpscaleCatalog.Fingerprint(_project) != job.Fingerprint || _project.PreviewStart != job.Project.PreviewStart || _project.PreviewLength != job.Project.PreviewLength)
            await RestoreProject(UpscaleStore.Clone(job.Project));
        _project.LastJob = UpscaleStore.JobPath(job);
        using var restoredOperation = _operation is null ? CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token, ApplicationBackgroundOperations.ExitToken) : null;
        if (restoredOperation is not null) { _operation = restoredOperation; token = restoredOperation.Token; }
        SetBusy(true);
        try {
            var progress = new Progress<UpscaleProgress>(p => {
                if (_closed) return; _progress.Value = p.Fraction;
                var label = _l(p.Detail).Replace("VideoUpscale.Decoding", T("Decoding"), StringComparison.Ordinal);
                if (p.Detail.Contains("/", StringComparison.Ordinal) || p.Detail.StartsWith("VideoUpscale.", StringComparison.Ordinal)) _status.Text = label;
                _log.AppendText(label + Environment.NewLine); if (_log.Text.Length > 40000) _log.Text = _log.Text[^20000..]; _log.ScrollToEnd();
            });
            var path = await new UpscalePipeline().Start(job, models, progress, token, restored, T("Title"));
            if (job.Preview) { _project.PreviewPath = path; _project.PreviewFingerprint = UpscaleCatalog.Fingerprint(job.Project) + "|" + UpscaleMediaTools.N(job.Project.PreviewStart) + "|" + UpscaleMediaTools.N(job.Project.PreviewLength); await ResultProxy(path); }
            else _project.ExportPath = path;
            Save();
        } finally { if (restoredOperation is not null) _operation = null; SetBusy(false); }
    }
    private async Task Resume()
    {
        if (_busy) { if (ApplicationBackgroundOperations.Current is {} host && host.State?.Kind == UpscalePipeline.Kind) await host.ResumeAsync(_lifetime.Token); return; }
        if (_project.LastJob is not {} file || !File.Exists(file)) return;
        var job = UpscaleStore.Load<UpscaleJob>(file);
        if (job.Complete) { _status.Text = T("Complete"); return; }
        _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, ApplicationBackgroundOperations.ExitToken);
        try {
            var models = PrepareModels is null ? "" : await PrepareModels(job.Project, _operation.Token);
            if (models is not null) await ExecuteJob(job, models, _operation.Token);
        } finally { _operation.Dispose(); _operation = null; }
    }
    private async Task ExportAvailable()
    {
        if (_busy || _project.LastJob is not {} file || !File.Exists(file)) return;
        var source = UpscaleStore.Load<UpscaleJob>(file);
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = source.Project.Codec == "ffv1" ? "Matroska (*.mkv)|*.mkv" : "MP4 (*.mp4)|*.mp4|Matroska (*.mkv)|*.mkv", FileName = _project.Name + "-partial", DefaultExt = source.Project.Codec == "ffv1" ? ".mkv" : ".mp4" };
        if (dialog.ShowDialog(this) != true) return;
        await ExecutePartial(UpscaleAssembly.Available(source, dialog.FileName), _lifetime.Token);
    }
    internal async Task ExecutePartial(UpscaleJob job, CancellationToken token, BackgroundOperationState? restored = null)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token, ApplicationBackgroundOperations.ExitToken);
        _operation = operation; SetBusy(true);
        try {
            await UpscaleAssembly.StartPartial(job, new Progress<UpscaleProgress>(p => { _progress.Value = p.Fraction; _status.Text = _l(p.Detail); }), operation.Token, T("ExportAvailable"), restored);
        } finally { _operation = null; SetBusy(false); }
    }
}
