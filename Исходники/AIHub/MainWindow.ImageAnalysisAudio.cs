using System.IO;
using AIHub.Models;
using AIHub.Services;

namespace AIHub;

public partial class MainWindow
{
    private SessionAudioPlayer? _sessionAudioPlayer;

    private SessionAudioPlayer GetSessionAudioPlayer()
    {
        if (_sessionAudioPlayer is not null) return _sessionAudioPlayer;
        _sessionAudioPlayer = new SessionAudioPlayer();
        _sessionAudioPlayer.Changed += RefreshSessionAudioUi;
        _sessionAudioPlayer.Failed += ShowHeavySpeechError;
        ImageAnalysisWorkspacePage.ExportAudioRequested += (_, _) => ExportSessionAudio();
        return _sessionAudioPlayer;
    }

    private void RefreshSessionAudioUi()
    {
        ImageAnalysisWorkspacePage.SetAudioPlaybackState(
            _sessionAudioPlayer?.HasAudio == true, _sessionAudioPlayer?.IsPlaying == true);
    }

    private async void ExportSessionAudio()
    {
        if (_sessionAudioPlayer?.HasAudio != true) return;
        var picker = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "WAV (*.wav)|*.wav", DefaultExt = ".wav", FileName = "description.wav",
            Title = L("ImageAnalysis.Workspace.Audio.Export")
        };
        if (picker.ShowDialog(this) != true) return;
        try { await _sessionAudioPlayer.ExportDescriptionAsync(picker.FileName); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { ShowHeavySpeechError(ex.Message); }
    }
}
