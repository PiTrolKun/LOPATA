using System.IO;
using System.Text.Json;
using System.Windows;
using AIHub.Services;
using AIHub.Services.VideoEditor;
using Microsoft.Win32;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace AIHub.Controls.VideoEditor;

internal sealed partial class VideoEditorWindow
{
    private VideoClipboard _copiedClips = new([], []);
    private void CopyClips() => _copiedClips = VideoSelectionEditing.Capture(Project, _timeline.Selection.Items);
    private void PasteClips() { if (_copiedClips.Empty) return; Edit(p => _timeline.SelectItems(VideoSelectionEditing.Paste(p, _copiedClips, _position))); }
    private static VideoClip? Selected(VideoProject p, string? id) => p.Tracks.SelectMany(t => t.Clips).FirstOrDefault(c => c.Id == id);
    private VideoClip? Selected(VideoProject p) => Selected(p, _timeline.SelectedClip);
    private async Task ImportAsync()
    {
        if (_busy) return;
        var picker = new OpenFileDialog { Title = T("Import"), Multiselect = true, Filter = "Media|*.mp4;*.mkv;*.webm;*.avi;*.mov;*.wav;*.mp3;*.flac;*.ogg;*.opus;*.aac;*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.svg|All files|*.*" };
        if (picker.ShowDialog(this) != true) return;
        _busy = true; var eventsImported = false; _localOperationCancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        try
        {
            using var audit = ComponentAudit.CaptureOperation(); await VideoRuntime.PrepareAsync(MediaToken);
            foreach (var file in picker.FileNames)
            {
                _status.Text = T("Preparing") + " " + Path.GetFileName(file);
                var asset = await VideoImport.ReadAsync(file, VideoPreviewAssets.DirectoryFor(Project), MediaToken);
                var streams = ChooseStreams(asset); if (!streams.Continue) continue;
                var position = Project.Tracks.SelectMany(t => t.Clips).Where(c => c.AssetId is {} id && Project.Asset(id).Kind == "video").Select(c => c.Start + c.Duration).DefaultIfEmpty(_position).Max();
                var events = asset.Kind == "video" ? await VideoAnalysis.DetectAsync(asset, Path.Combine(VideoPreviewAssets.DirectoryFor(Project), "events-" + asset.Id), MediaToken) : [];
                var imported = await ImportSubtitlesAsync(asset, streams.Subtitles, position);
                _editing.Edit(p => {
                    InsertSelectedMaterial(p, asset, position); p.Narration.AddRange(imported);
                    if (events.Count > 0) {
                        var clip = p.Tracks.Where(t => t.Kind == "video").SelectMany(t => t.Clips).Single(c => c.AssetId == asset.Id);
                        VideoEventCuts.Apply(p, clip.Id, events, streams.Subtitles.Length == 0);
                        eventsImported = true;
                    }
                });
                _dirty = true;
            }
            await PrepareMediaAsync(); await RefreshAsync(); VideoProjectStore.Save(Project, VideoProjectStore.Recovery(Project.Id)); UpdateTitle(); if (eventsImported) ShowEventSummary();
        }
        finally { _busy = false; _localOperationCancel?.Dispose(); _localOperationCancel = null; if (!_closed) await RefreshAsync(); }
    }
    private async Task OpenProjectAsync()
    {
        if (_busy) return; var picker = new OpenFileDialog { Filter = "LOPATA Video|*.lopataVideo", Title = T("OpenProject") };
        if (picker.ShowDialog(this) != true) return;
        await LoadProjectAsync(picker.FileName);
    }
    private async Task LoadProjectAsync(string path)
    {
        if (_busy) throw new InvalidOperationException("VideoEditor.Busy");
        VideoProject next; _busy = true; _status.Text = T("OpeningArchive");
        try { next = await Task.Run(() => VideoProjectStore.Load(path), _lifetime.Token); }
        finally { _busy = false; }
        await LoadExternalProjectAsync(next, path);
    }
    private async Task RecoverAsync()
    {
        if (_busy) return;
        var picker = new OpenFileDialog { Title = T("Recovery"), Filter = "LOPATA Video|*.lopataVideo", InitialDirectory = VideoProjectStore.Root };
        if (picker.ShowDialog(this) != true) return;
        var next = VideoProjectStore.Load(picker.FileName); VideoProjectStore.Save(RecoveryProject, VideoProjectStore.Recovery(Project.Id));
        _editing = new(next); _path = null; _position = 0; _dirty = true; _selectedSegment = null; await PrepareMediaAsync(); await RefreshAsync(); UpdateTitle();
        _saveChanges = new(Project, false, recovered: true);
    }
    private void Save(bool asNew) => Safe(() => SaveArchiveAsync(asNew));
    private void Undo(bool redo) { if (_busy) return; if (redo) _editing.Redo(); else _editing.Undo(); _dirty = _saveChanges.HasChanges(Project); _saveTimer.Start(); UpdateTitle(); Safe(RefreshAsync); }
    private void DeleteClip() => Edit(p => VideoSelectionEditing.Delete(p, _timeline.Selection.Items));
    private void DuplicateClip() { var bundle = VideoSelectionEditing.Capture(Project, _timeline.Selection.Items); if (!bundle.Empty) Edit(p => { _ = VideoSelectionEditing.Clips(p, _timeline.Selection.Items); _timeline.SelectItems(VideoSelectionEditing.Paste(p, bundle, bundle.End)); }); }
    private void InsertMaterial() { if (_materials.SelectedItem is VideoAsset asset) Edit(p => InsertSelectedMaterial(p, p.Asset(asset.Id), _position)); }
    private async Task RelinkAsync()
    {
        if (_busy || _materials.SelectedItem is not VideoAsset old) return;
        var picker = new OpenFileDialog { Title = T("Relink"), CheckFileExists = true }; if (picker.ShowDialog(this) != true) return;
        await VideoRuntime.PrepareAsync(_lifetime.Token); var replacement = await VideoImport.ReadAsync(picker.FileName, VideoPreviewAssets.DirectoryFor(Project), _lifetime.Token);
        if (replacement.Kind != old.Kind) throw new InvalidDataException("VideoEditor.UnsupportedMedia"); replacement.Id = old.Id;
        // Only the managed preview is invalidated; the previous user source is never touched.
        foreach (var proxy in new[] { VideoPreviewAssets.ProxyPath(Project, old), VideoPreviewAssets.AudioPath(Project, old) })
            if (File.Exists(proxy)) File.Move(proxy, proxy + ".old-" + Guid.NewGuid().ToString("N"));
        Edit(p => p.Assets[p.Assets.FindIndex(a => a.Id == old.Id)] = replacement); await PrepareMediaAsync(); await RefreshAsync();
    }
    private async Task RunWorkAsync(string action, bool selected = false, bool whole = false, string? destination = null, VideoExportOptions? export = null, string? brief = null, VideoProject? project = null)
    {
        if (_busy || ApplicationBackgroundOperations.Current?.HasPending == true) { _status.Text = T("Busy"); return; }
        var selectedFragments = SelectedFragments();
        if (selected && selectedFragments.Length == 0) { _status.Text = T("NoFragment"); return; }
        var musicSeconds = Project.Duration; _busy = true;
        _localOperationCancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        using var audit = ComponentAudit.CaptureOperation();
        VideoPreparedModels? prepared = null;
        try {
            if (action is "music" or "analyze" or "speech" && PrepareModels is not null) {
                _status.Text = T("CheckingModels"); _progress.IsIndeterminate = true;
                prepared = await PrepareModels(ModelNeed(Project, action, selected ? selectedFragments : null, whole), MediaToken);
                if (prepared is null) return;
            }
            if (action is "analyze" or "svg" && EnsureRuntime is not null) await EnsureRuntime();
            CommitSpacing(); _playing = false; await _preview.PlayAsync(false);
            UpdateVoiceSummary();
            var id = Guid.NewGuid().ToString("N"); _operationId = id;
            var directory = Path.Combine(VideoProjectStore.Work(Project.Id), "Operations", id); Directory.CreateDirectory(directory);
            var input = Path.Combine(directory, "input.lopataVideo"); VideoProjectStore.Save(project ?? Project.Clone(), input);
            var request = new VideoWorkRequest(id, input, action, destination, export, selected ? selectedFragments : null, brief ?? _brief.Text, Project.DefaultVoice, Project.DefaultLanguage, Project.DefaultRate, whole,
                action == "music" ? new VideoMusicSpec(prepared?.ModelsRoot ?? throw new InvalidOperationException("VideoEditor.StorageRequired"), musicSeconds, T("BackgroundMusicTrack")) : null, Project.DefaultVoiceOptions);
            try
            {
                var result = await _work.RunAsync(request, _lifetime.Token);
                if (action != "export") { var next = VideoProjectStore.Load(result); _editing.Edit(p => { p.Assets = next.Assets; p.Tracks = next.Tracks; p.Narration = next.Narration; }); _dirty = true; await PrepareMediaAsync(); await RefreshAsync(); VideoProjectStore.Save(Project, VideoProjectStore.Recovery(Project.Id)); }
                _status.Text = T("Done") + " " + (action == "export" ? result : ""); if (action == "detect") ShowEventSummary(); UpdateTitle();
                var longCount = Project.Tracks.SelectMany(t => t.Clips).Count(c => VideoEditing.TooLong(Project, c));
                if (action == "speech" && longCount > 0) _status.Text = string.Format(T("SpeechLongSummary"), longCount);
            }
            finally { CloseSvgProgress(); }
        }
        finally { _busy = false; _progress.IsIndeterminate = false; _localOperationCancel?.Dispose(); _localOperationCancel = null; }
    }
    private void ShowEventSummary() => _status.Text = string.Format(T("EventsReady"), Project.Tracks.Where(t => t.Kind == "video").Sum(t => t.Clips.Count), Project.Narration.Count);
    private async Task GenerateSvgAsync()
    {
        var values = Ask("GenerateSvg", [("Prompt", "")]); if (values is null || string.IsNullOrWhiteSpace(values["Prompt"])) return;
        await RunWorkAsync("svg", brief: values["Prompt"]);
    }
    private void ImportSrt()
    {
        var picker = new OpenFileDialog { Filter = "SubRip|*.srt", Title = T("ImportSrt") }; if (picker.ShowDialog(this) != true) return;
        var rows = VideoSubtitles.Parse(File.ReadAllText(picker.FileName)); var file = new FileInfo(picker.FileName);
        Edit(p => { p.Narration.AddRange(rows); if (!p.ImportedFiles.Any(f => f.Path.Equals(file.FullName, StringComparison.OrdinalIgnoreCase))) p.ImportedFiles.Add(new(file.FullName, file.Name, file.Length, file.LastWriteTimeUtc)); });
    }
    private async Task ExportAsync()
    {
        var tracks = Project.Narration.GroupBy(TrackKey).ToDictionary(g => "@" + g.First().Language + " · " + g.First().SubtitleTitle + " [" + g.Key + "]", g => g.Key);
        var values = Ask("Export", [("Width", Project.Width.ToString()), ("Height", Project.Height.ToString()), ("Fps", VideoExport.N(Project.Fps)), ("From", "0"), ("To", VideoExport.N(Project.Duration)), ("Bitrate", "12")], new[] { "EmbeddedSubtitles", "SeparateSrt" }.Concat(tracks.Keys).ToArray());
        if (values is null) return;
        var picker = new SaveFileDialog { Filter = "MPEG-4|*.mp4|Matroska|*.mkv", FileName = Project.Name + "-export", DefaultExt = ".mp4", Title = T("Export"), OverwritePrompt = false };
        if (picker.ShowDialog(this) != true) return;
        var options = new VideoExportOptions((int)Number(values["Width"]), (int)Number(values["Height"]), Number(values["Fps"]), Number(values["From"]), Number(values["To"]), values["EmbeddedSubtitles"] == "true", values["SeparateSrt"] == "true", (int)Number(values["Bitrate"]));
        var snapshot = Project.Clone(); snapshot.Narration.RemoveAll(n => !tracks.Any(t => t.Value == TrackKey(n) && values[t.Key] == "true"));
        VideoExport.Validate(snapshot, picker.FileName, options); await RunWorkAsync("export", destination: picker.FileName, export: options, project: snapshot);
    }
}
