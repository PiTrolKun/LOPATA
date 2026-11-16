namespace AIHub.Controls.OfficeTextEditor;

internal sealed partial class OfficeTextEditorView
{
    internal string? CrashRecoveryDraftId => _ready && !_closed ? _work?.DraftId : null;
}
