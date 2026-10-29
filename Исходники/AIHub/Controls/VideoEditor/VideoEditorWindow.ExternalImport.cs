using System.IO;
using AIHub.Services;
using AIHub.Services.VideoEditor;

namespace AIHub.Controls.VideoEditor;

internal sealed partial class VideoEditorWindow
{
    internal async Task ImportGeneratedAsync(string path)
    {
        if (_busy) return;
        _busy = true; _localOperationCancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        try
        {
            using var audit = ComponentAudit.CaptureOperation(); await VideoRuntime.PrepareAsync(MediaToken);
            var asset = await VideoImport.ReadAsync(path, VideoPreviewAssets.DirectoryFor(Project), MediaToken);
            var streams = ChooseStreams(asset); if (!streams.Continue) return;
            var at = Project.Tracks.SelectMany(t => t.Clips).Select(c => c.Start + c.Duration).DefaultIfEmpty(0).Max();
            _editing.Edit(p => InsertSelectedMaterial(p, asset, at)); _dirty = true;
            await PrepareMediaAsync(); await RefreshAsync();
            VideoProjectStore.Save(Project, VideoProjectStore.Recovery(Project.Id)); UpdateTitle();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { VideoErrors.Report(error); _status.Text = T("OpenFailed"); }
        finally { _busy = false; _localOperationCancel.Dispose(); _localOperationCancel = null; if (!_closed) await RefreshAsync(); }
    }
}
