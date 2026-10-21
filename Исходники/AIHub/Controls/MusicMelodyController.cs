using System.Windows;
using AIHub.Services.MusicMidi;

namespace AIHub.Controls;

public sealed class MusicMelodyController
{
    private readonly MusicWorkspaceControl _view;
    private Func<string,string> _l = k => k;
    public MusicMelodyController(MusicWorkspaceControl view) {
        _view = view; view.Generation.MelodyButton.Click += (_,_) => Open();
        view.Generation.MelodyButton.PreviewMouseRightButtonUp += (_,e) => {
            e.Handled = true; if (view.Projects.Busy) return;
            var previous = view.Generation.Melody;
            if (previous.Notes.Length == 0) { Open(); return; }
            try {
                var next = previous with { Enabled = !previous.Enabled };
                MusicMelodyPipeline.Validate(next, view.Generation.AudioTask, view.Generation.Variation);
                view.Generation.Melody = next; view.Projects.SaveLoraWorkspace();
            } catch (Exception error) { view.Generation.Melody = previous; Error(error); }
        };
    }
    public void Localize(Func<string,string> l) => _l = l;
    private void Open() {
        if (_view.Projects.Busy) return;
        try {
            var window = new MusicMelodyWindow(_view.Generation.Melody, _view.Generation.Variation, _l) { Owner = Window.GetWindow(_view) };
            if (window.ShowDialog() != true) return;
            MusicMelodyPipeline.Validate(window.Result, _view.Generation.AudioTask, _view.Generation.Variation);
            var previous = _view.Generation.Melody;
            try { _view.Generation.Melody = window.Result; _view.Projects.SaveLoraWorkspace(); }
            catch { _view.Generation.Melody = previous; throw; }
        } catch (Exception error) { Error(error); }
    }
    private void Error(Exception error) => System.Windows.MessageBox.Show(Window.GetWindow(_view),
        error.Message.StartsWith("Music.Midi.") ? _l(error.Message) : error.Message, _l("Music.Midi.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
}
