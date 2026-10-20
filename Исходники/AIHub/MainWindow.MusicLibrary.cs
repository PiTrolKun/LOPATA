using System.Text.Json;
using System.IO;
using AIHub.Controls;
using AIHub.Services;
using AIHub.Services.MusicLibrary;

namespace AIHub;

public partial class MainWindow
{
    private void RegisterLyricLibraryBackgroundOperation()
    {
        LyricLibraryPreparation.RequestPreparation = (root, token) => Dispatcher.InvokeAsync(() =>
            BackgroundUserDecision.ShowDialog(new LyricLibraryPreparationWindow(this, root, L, token), this, token) == true).Task;
        _backgroundOperations!.Register(LyricLibrarySearch.BackgroundKind, (state, token) => {
            var job = state.Input.Deserialize<LyricIndexJob>() ?? throw new InvalidDataException("Music.Library.InvalidRecord");
            if (job.Directory != LyricLibraryStore.Default.DirectoryPath || string.IsNullOrWhiteSpace(job.ModelsRoot))
                throw new InvalidDataException("Music.Library.InvalidRecord");
            return LyricLibrarySearch.BuildOperationAsync(job, new InlineProgress<RagBookProgress>(_ => { }), token, state);
        });
    }
    private void ViewLyricLibrary()
    {
        RestoreFromTray();
        var library = new LyricLibraryWindow(this, L, allowImport: false); library.Show();
    }
}
