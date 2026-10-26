using System.Windows;
using AIHub.Services.VideoEditor;
using CheckBox = System.Windows.Controls.CheckBox;

namespace AIHub.Controls.VideoEditor;

internal sealed partial class VideoEditorWindow
{
    private readonly CheckBox _spacing = new() { Margin = new(4) };
    private VideoSpacingSession? _spacingSession;
    private bool _spacingWasDirty;
    private VideoProject RecoveryProject => _spacingSession is { Untouched: true } session && session.BelongsTo(_editing) ? session.Original : Project;
    private void ToggleSpacing()
    {
        if (_busy) { _spacing.IsChecked = _spacingSession is not null; _status.Text = T("Busy"); return; }
        try {
            if (_spacing.IsChecked == true) { _spacingWasDirty = _dirty; _spacingSession = new(_editing); }
            else EndSpacing();
            _status.Text = T(_spacing.IsChecked == true ? "SpacingPreview" : "Ready"); UpdateTitle(); Safe(RefreshAsync);
        }
        catch (Exception error) { _spacing.IsChecked = false; _spacingSession = null; _status.Text = Error(error); }
    }
    private void EndSpacing()
    {
        if (_spacingSession is {} session && session.BelongsTo(_editing)) { var untouched = session.Untouched; session.Finish(); _dirty = untouched ? _spacingWasDirty : true; }
        _spacingSession = null; _spacing.IsChecked = false;
    }
    private void CommitSpacing() { if (_spacingSession is not null) _dirty = true; _spacingSession = null; _spacing.IsChecked = false; }
}
