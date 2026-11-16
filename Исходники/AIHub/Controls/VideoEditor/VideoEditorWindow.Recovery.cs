namespace AIHub.Controls.VideoEditor;

internal sealed partial class VideoEditorWindow
{
    internal string? RecoveryFilePath => _path;
    internal double RecoveryPreviewPosition => _position;
    internal bool RecoveryHasUnsavedChanges => _dirty;

    internal void MarkCrashRecoveryDraft()
    {
        _dirty = true;
        _saveChanges = new(Project, _path is not null, recovered: true);
        UpdateTitle();
    }

    internal async Task RestoreRecoveryPreviewAsync(double position)
    {
        await Ready.Task.WaitAsync(_lifetime.Token);
        if (!_closed && !_busy && double.IsFinite(position) && position > 0)
            await SeekAsync(Math.Min(position, Project.Duration));
    }
}
