using System.IO;
using System.Windows;
using AIHub.Services;

namespace AIHub.Controls;

public sealed class MusicAudioTaskController
{
    private readonly MusicWorkspaceControl _view;
    private Func<string, string> _l = key => key;
    private bool _opening;
    public MusicAudioTaskController(MusicWorkspaceControl view)
    {
        _view = view; view.Generation.AudioTaskButton.Click += async (_, _) => await OpenAsync();
        view.Generation.AudioTaskButton.PreviewMouseRightButtonUp += (_, e) => {
            e.Handled = true; if (view.Projects.Busy) return;
            var task = view.Generation.AudioTask; var next = task with { Enabled = !task.Enabled };
            if (next.Enabled) {
                try { next.Validate(view.Generation.Variation); }
                catch (InvalidDataException) { _ = OpenAsync(); return; }
            }
            try { view.Generation.AudioTask = next; view.Projects.SaveLoraWorkspace(); }
            catch (Exception error) { view.Generation.AudioTask = task; System.Windows.MessageBox.Show(Window.GetWindow(view), error.Message, _l("Music.Transform.Title")); }
        };
    }
    public void Localize(Func<string, string> l) => _l = l;
    private async Task OpenAsync()
    {
        if (_opening || _view.Projects.Busy) return; _opening = true;
        try {
            var task = _view.Generation.AudioTask;
            var track = _view.Player.CurrentTrack;
            var current = track?.Path;
            if (track is { IsLayered: true }) current = await MusicStemPlayback.MixAsync(track, ApplicationBackgroundOperations.ExitToken);
            var editorSource = _view.Editor.CaptureSelection();
            var window = new MusicAudioTaskWindow(Window.GetWindow(_view), _view.Generation.Variation, task, _view.Editor.Lyrics, current, _l, text => AIHub.Services.MusicLibrary.LyricLibraryDraft.FromEditor(_view.Projects.Capture(), text, _l));
            if (window.ShowDialog() == true) {
                if (_view.Editor.Lyrics != window.Lyrics && !_view.Editor.ApplyEdit(editorSource,
                    new MusicTextEdit(0, editorSource.Text.Length, window.Lyrics, window.Lyrics.Length, 0))) throw new InvalidDataException("Music.Library.Changed");
                _view.Generation.AudioTask = window.Result;
                _view.Status.AppendLog(_l("Music.Transform.Applied"));
                _view.Projects.SaveLoraWorkspace();
            }
        } catch (Exception e) {
            System.Windows.MessageBox.Show(Window.GetWindow(_view), e.Message.StartsWith("Music.") ? _l(e.Message) : e.Message, _l("Music.Transform.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        } finally { _opening = false; }
    }
}
