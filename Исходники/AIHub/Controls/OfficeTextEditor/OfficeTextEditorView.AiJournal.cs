using AIHub.Services.OfficeEditor;

namespace AIHub.Controls.OfficeTextEditor;

internal sealed partial class OfficeTextEditorView
{
    internal OfficeAiJournal CreateAiJournal(string action) =>
        new(CurrentSheetId ?? OfficeSheets.PrimaryId, action, RecordAiJournalAsync);

    internal Task RecordAiJournalAsync(OfficeAiEntry entry, string? resultFile) =>
        Dispatcher.InvokeAsync(async () =>
        {
            await Work.UpsertAiEntryAsync(entry, _lifetime.Token, resultFile);
            RefreshProjectNotice();
        }).Task.Unwrap();
}
