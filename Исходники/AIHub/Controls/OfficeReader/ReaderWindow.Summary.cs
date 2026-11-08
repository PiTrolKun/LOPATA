using AIHub.Services.OfficeReader;

namespace AIHub.Controls.OfficeReader;

public sealed partial class ReaderWindow
{
    private System.Windows.Controls.Button _summary = null!;
    private void SummaryClicked()
    {
        var document = _document;
        if (document is null || _loading || _closed || _speaking || _translating) return;
        var selected = _selection.HasSelection;
        new OfficeSummaryWindow(this, _l, selected, token => selected
            ? _selection.ReadAsync(document, token)
            : new ReaderSpeechRange(0, document.Pages.Count).ReadAsync(document, token),
            CreateReaderAiJournal(_l("OfficeSummary.Title")), SourcePath).ShowDialog();
    }
}
