using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Controls;

public sealed partial class MusicPlayerControl
{
    private MusicStemPlaybackMode _stemMode;
    private CancellationTokenSource? _stemOpening;
    private string? _stemTemporary;
    private bool _stemLoading, _stemPlayOnReady;
    private TimeSpan? _stemPosition;

    private void ConfigureStemMenu()
    {
        _play.ContextMenu = null;
        if (_track?.IsLayered != true) return;
        var menu = new System.Windows.Controls.ContextMenu();
        foreach (var mode in Enum.GetValues<MusicStemPlaybackMode>()) {
            var item = new System.Windows.Controls.MenuItem { Header = _l("Music.Stems.Play." + mode), Tag = mode };
            AutomationProperties.SetAutomationId(item, "Music.Audio.Stems." + mode);
            item.Click += (_, _) => {
                if (_track?.IsLayered != true || _stemMode == mode) return;
                var position = _stemPosition ?? _audio.Position; var playing = _stemLoading ? _stemPlayOnReady : _audio.IsPlaying;
                _stemMode = mode; _ = OpenStemAsync(position, playing); Refresh(); PositionChanged?.Invoke();
            };
            menu.Items.Add(item);
        }
        _play.ContextMenu = menu;
    }
    private void RefreshStemIcon()
    {
        if (_track?.IsLayered != true) return;
        var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = _stemMode switch { MusicStemPlaybackMode.Voice => "🎤", MusicStemPlaybackMode.Music => "🎼", _ => "🔊" },
            FontSize = 17, VerticalAlignment = VerticalAlignment.Center });
        var play = MusicAudioUi.Icon(_audio.IsPlaying || _waitingPlay ? "M7,3 V21 M17,3 V21" : "M7,3 L21,12 L7,21 Z");
        play.Width = play.Height = 12; row.Children.Add(play); _play.Content = row;
        MusicAudioUi.Label(_play, _l(_audio.IsPlaying ? "Music.Audio.Pause" : "Music.Audio.Play") + " · " + _l("Music.Stems.Play." + _stemMode) + " · " + _l("Music.Stems.PlayHint"));
    }
    private async Task OpenStemAsync(TimeSpan position, bool play)
    {
        var track = _track; if (track?.IsLayered != true) return;
        _stemOpening?.Cancel(); _stemOpening?.Dispose();
        var cancellation = new CancellationTokenSource(); _stemOpening = cancellation;
        var token = cancellation.Token;
        _stemPosition = position; _stemPlayOnReady = play; _stemLoading = true; _errorKey = "";
        _audio.Open(null); DeleteStemTemporary(); Refresh();
        string? mixed = null;
        try {
            await MusicStemPlayback.VerifyAsync(track, token).ConfigureAwait(false);
            var path = _stemMode switch { MusicStemPlaybackMode.Voice => track.Path, MusicStemPlaybackMode.Music => track.MusicPath, _ => mixed = await MusicStemPlayback.MixAsync(track, token).ConfigureAwait(false) };
            token.ThrowIfCancellationRequested();
            if (Dispatcher.HasShutdownStarted) return;
            await Dispatcher.InvokeAsync(() => {
                if (_disposed || _stemOpening != cancellation || _track != track) return;
                _stemTemporary = mixed; mixed = null; _audio.Open(path); Refresh();
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or TimeoutException) {
            if (!Dispatcher.HasShutdownStarted) await Dispatcher.InvokeAsync(() => {
                if (!_disposed && _stemOpening == cancellation) { _stemLoading = false; _stemPosition = null; _stemPlayOnReady = false; Failed("Music.Audio.Error"); }
            });
        }
        finally { if (mixed is not null && File.Exists(mixed)) File.Delete(mixed); }
    }
    private void CompleteStemOpen()
    {
        if (!_stemLoading || !_audio.IsReady || _stemPosition is not { } position) return;
        _stemPosition = null; _stemLoading = false;
        var play = _stemPlayOnReady; _stemPlayOnReady = false;
        _audio.Seek(position < _audio.Duration ? position : TimeSpan.Zero);
        if (play && !_lyricsPending && _surfaceActive && !_audio.IsPlaying) _audio.Toggle();
        else if (play && _lyricsPending) _waitingPlay = true;
        PlaybackSeeked?.Invoke();
    }
    private void ResetStemPlayback()
    {
        _stemOpening?.Cancel(); _stemOpening?.Dispose(); _stemOpening = null;
        _stemLoading = _stemPlayOnReady = false; _stemPosition = null;
        // Close first, including the player's owned decode, before deleting its input cache.
        if (_stemTemporary is not null) _audio.Open(null);
        DeleteStemTemporary();
    }
    private void DeleteStemTemporary()
    {
        var path = _stemTemporary; _stemTemporary = null;
        if (path is null) return;
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    private async Task SaveStemFolderAsync(MusicTrack track)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = _l("Music.Stems.SaveFolder"), InitialDirectory = Directory.Exists(_outputFolder) ? _outputFolder : "" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try {
            await MusicStemFolder.CopyAsync(track.BundleFolder!, dialog.FolderName, CancellationToken.None);
            if (!_disposed) _status.Text = _l("Music.Audio.Saved");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) {
            if (!_disposed) _status.Text = _l("Music.Audio.FileError") + " " + error.Message;
        }
    }
}
