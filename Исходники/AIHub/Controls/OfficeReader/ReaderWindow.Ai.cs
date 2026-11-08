namespace AIHub.Controls.OfficeReader;

public sealed partial class ReaderWindow
{
    private void OpenAiSettings() => new ReaderAiWindow(this, _l).ShowDialog();
}
