using System.IO;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using AIHub.Services.VideoEditor;

namespace AIHub.Controls.VideoEditor;

internal sealed partial class VideoEditorWindow
{
    private bool _savingArchive;
    internal async Task LoadExternalProjectAsync(VideoProject project, string path)
    {
        if (_busy) throw new InvalidOperationException("VideoEditor.Busy");
        _busy = true;
        try {
            VideoProjectStore.Save(RecoveryProject, VideoProjectStore.Recovery(Project.Id));
            _editing = new(project); _path = path; _position = 0; _dirty = false; _selectedSegment = null;
            _saveChanges = new(Project, true);
            await PrepareMediaAsync(); await RefreshAsync(); UpdateTitle();
        }
        finally { _busy = false; }
    }
    private async Task<bool> SaveArchiveAsync(bool asNew)
    {
        if (_busy) { _status.Text = T("Busy"); return false; } var destination = _path;
        if (destination is null || asNew) {
            if (SelectArchiveDestination is not null) destination = SelectArchiveDestination();
            else {
                var picker = new SaveFileDialog { Filter = "LOPATA Video|*.lopataVideo", DefaultExt = ".lopataVideo", FileName = Project.Name, Title = T("SaveAs"), OverwritePrompt = true };
                if (picker.ShowDialog(this) != true) return false; destination = picker.FileName;
            }
            if (string.IsNullOrWhiteSpace(destination)) return false;
        }
        CommitSpacing(); _saveTimer.Stop(); var snapshot = Project.Clone(); snapshot.Name = Path.GetFileNameWithoutExtension(destination);
        _busy = _savingArchive = true; _localOperationCancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _status.Text = T("SavingArchive"); _progress.IsIndeterminate = false; _progress.Value = 0;
        var progress = new Progress<double>(value => { if (!_closed && _savingArchive) _progress.Value = value; });
        try {
            await Task.Run(() => VideoProjectArchive.Save(snapshot, destination, MediaToken, progress), MediaToken);
            _path = destination; Project.Name = snapshot.Name; Project.Provenance = VideoProjectStore.Provenance(Project); _saveChanges.Saved(Project); _dirty = false; UpdateTitle(); _status.Text = T("SavedArchive");
            return true;
        }
        finally { _savingArchive = _busy = false; _localOperationCancel?.Dispose(); _localOperationCancel = null; if (!_closed) _progress.Value = 0; }
    }
}
