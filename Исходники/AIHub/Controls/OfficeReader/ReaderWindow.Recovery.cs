using System.Windows.Threading;

namespace AIHub.Controls.OfficeReader;

public sealed partial class ReaderWindow
{
    internal string? CrashRecoveryDraftId => _editorReady ? _editor?.CrashRecoveryDraftId : null;

    internal async Task RestoreCrashRecoveryReadingAsync()
    {
        await Dispatcher.Yield(DispatcherPriority.Loaded);
        await _modeChange;
        if (!_closed && _editorReady && IsEditing && _editor is { } editor)
            await (_modeChange = ShowEditedDocumentAsync(editor));
    }
}
