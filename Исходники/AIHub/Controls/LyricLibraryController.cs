using System.Windows;
using System.Windows.Controls;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = System.Windows.MessageBox;
using AIHub.Services.MusicLibrary;

namespace AIHub.Controls;

public sealed class LyricLibraryController
{
    private readonly MusicWorkspaceControl _view;
    private Func<string, string> _l = key => key;
    public LyricLibraryController(MusicWorkspaceControl view)
    { _view = view; view.TextActions.LibraryRequested += OpenMenu;
        view.Editor.LibraryOffer.Draft = () => LyricLibraryDraft.FromEditor(view.Projects.Capture(), view.Editor.Lyrics, _l); }
    public void Localize(Func<string, string> localize) => _l = localize;
    private void OpenMenu()
    {
        var menu = new ContextMenu();
        var import = new MenuItem { Header = _l("Music.Library.Import") }; import.Click += (_, _) => Open(false);
        var save = new MenuItem { Header = _l("Music.Library.SaveToLibrary") }; save.Click += (_, _) => Open(true);
        import.IsEnabled = !_view.Projects.Busy; save.IsEnabled = !_view.Projects.Busy && !string.IsNullOrWhiteSpace(_view.Editor.Lyrics);
        menu.Items.Add(import); menu.Items.Add(save); menu.PlacementTarget = _view.TextActions.LibraryButton; menu.IsOpen = true;
    }
    private void Open(bool save)
    {
        var source = _view.Editor.CaptureSelection();
        var snapshot = _view.Projects.Capture() with { RhymeSteps = _view.Rhyme.SnapshotHistory() };
        var draft = save ? LyricLibraryDraft.FromEditor(snapshot, source.Text, _l) : null;
        var window = new LyricLibraryWindow(Window.GetWindow(_view), _l, allowImport: !save, draft: draft);
        if (window.ShowDialog() == true && window.ImportedEntry is { } entry)
        {
            var edit = new AIHub.Services.MusicTextEdit(source.Start, source.Length, entry.Text, source.Start + entry.Text.Length, 0);
            if (!_view.Editor.ApplyEdit(source, edit))
                MessageBox.Show(Window.GetWindow(_view), _l("Music.Library.Changed"), _l("Music.Library.Library"));
        }
    }
}
