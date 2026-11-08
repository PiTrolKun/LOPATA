using AIHub.Controls.OfficeReader;
using AIHub.Services.OfficeReader;

namespace AIHub;

public partial class MainWindow
{
    internal void AcceptReaderFile(ReaderFileRequest request)
    {
        request = request.Validate();
        // Acknowledge the IPC hand-off as soon as the window exists. Conversion and
        // first-page rendering are cancellable window work, not a pipe operation.
        ReaderWindowGroup.For(this, L, _isDarkTheme).Open(request.Path);
    }

    private void OpenOfficeReader()
    {
        ReaderWindowGroup.For(this, L, _isDarkTheme).Open();
    }

    private void OpenOfficeEditor() => ReaderWindowGroup.For(this, L, _isDarkTheme).OpenEditor();

    private async Task<bool> CloseOfficeEditorsAsync()
    {
        // Closing an owner can close its WPF children without their normal close prompt.
        foreach (var window in OwnedWindows.OfType<ReaderWindow>().ToArray())
            if (!await window.CloseEditorAsync()) return false;
        return true;
    }
}
