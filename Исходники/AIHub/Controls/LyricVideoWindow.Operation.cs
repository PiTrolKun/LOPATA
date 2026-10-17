using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Services;
using AIHub.Services.LyricVideo;

namespace AIHub.Controls;

public sealed partial class LyricVideoWindow
{
    private async Task LoadAsync()
    {
        _status.Text = _l("LyricVideo.Preparing");
        try
        {
            _session = await Task.Run(() => ClipSession.LoadAsync(_config, null, _lifetime.Token), _lifetime.Token);
            if (_closing) return;
            _renderer = new(_session, _options); _bitmap = new(1920, 1080, 96, 96, PixelFormats.Bgra32, null);
            _image.Source = _bitmap; _seek.Maximum = _session.TotalDuration; _player.Volume = .7;
            _status.Text = _l(_session.Lyrics is null ? "LyricVideo.NoTiming" : "LyricVideo.Ready");
            _player.Open(new Uri(_session.WavePath)); _timer.Start();
        }
        catch (OperationCanceledException) { Ready.TrySetResult(); }
        catch (Exception error)
        {
            if (!_closing) { _status.Text = _l("LyricVideo.Failed") + " " + LocalError(error); Ready.TrySetResult(); }
        }
    }
    private async Task ApplySettingsAsync()
    {
        if (_closing) return;
        if (_applying) { _settingsTimer.Start(); return; }
        _applying = true;
        try
        {
            var next = _settings.Options; next.Validate();
            _preferences.Save(next);
            if (_frameWork is not null) await _frameWork;
            if (_closing) return;
            _options = next;
            if (_session is not null) { _renderer?.Dispose(); _renderer = new(_session, next); _lastFrame = -1; RequestFrame(); }
        }
        catch (Exception error) { if (!_closing) _status.Text = _l("LyricVideo.PreferencesError") + " " + LocalError(error); }
        finally { _applying = false; }
    }
    private async Task ExportAsync()
    {
        if (_session is null || _exportWork is { IsCompleted: false }) return;
        if (ApplicationBackgroundOperations.Current is { } controller && (controller.IsRunning || controller.BlocksNewWork))
        { _status.Text = _l("LyricVideo.OtherOperation"); return; }
        var options = _settings.Options; options.Validate();
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = _l("LyricVideo.Export"), Filter = "MP4|*.mp4", DefaultExt = ".mp4",
            FileName = Path.GetFileNameWithoutExtension(_config.Song) + ".mp4", InitialDirectory = Path.GetDirectoryName(_config.Song), OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true) return;
        if (File.Exists(dialog.FileName)) { _status.Text = _l("LyricVideo.NewFileRequired"); return; }
        if (_playing) Toggle();
        _settingsTimer.Stop(); await ApplySettingsAsync();
        _export.IsEnabled = false; _cancel.IsEnabled = true; _settings.IsEnabled = false;
        _exportCancellation = new CancellationTokenSource();
        var request = new ClipRequest(Guid.NewGuid().ToString("N"), _config.Song, _session.SourceHash, Path.GetFullPath(dialog.FileName), options, _config.Language);
        _requestId = request.Id;
        try
        {
            _exportWork = _work.RunAsync(request, _exportCancellation.Token); await _exportWork;
        }
        catch (OperationCanceledException) { if (!_closing) _status.Text = _l("LyricVideo.Cancelled"); }
        catch (Exception error) { if (!_closing) _status.Text = _l("LyricVideo.Failed") + " " + LocalError(error); }
        finally
        {
            _exportCancellation.Dispose(); _exportCancellation = null;
            if (!_closing) { _export.IsEnabled = true; _cancel.IsEnabled = false; _settings.IsEnabled = true; }
        }
    }
    private void WorkChanged(ClipProgress progress)
    {
        if (_closing || (progress.Id != _requestId && !progress.Source.Equals(_config.Song, StringComparison.OrdinalIgnoreCase))) return;
        Dispatcher.BeginInvoke(() => {
            if (_closing) return;
            _progress.Value = progress.Progress; _status.Text = _l(progress.Status)
                + (progress.Status == "LyricVideo.Exporting" ? $" {progress.Progress:P0}" : "");
            if (progress.Result is not null) { _result = progress.Result; _openResult.Visibility = Visibility.Visible; _status.Text += " " + progress.Result; }
        });
    }
    private string LocalError(Exception error)
    {
        var message = error.Message;
        if (message.StartsWith("LyricVideo.", StringComparison.Ordinal)) return _l(message);
        OwnedProcessRegistry.Log("music_video_window_failed", "Music.Video", detail: error.GetType().Name);
        return _l("LyricVideo.CheckComponents");
    }
    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true; if (_closing) return;
        _closing = true; _timer.Stop(); _settingsTimer.Stop(); _lifetime.Cancel(); _player.Close();
        _work.Changed -= WorkChanged;
        // Durable export belongs to the application and can continue in the tray after this window closes.
        try { await Task.WhenAll(new[] { _loadWork, _frameWork }.OfType<Task>()); } catch (Exception) { }
        _renderer?.Dispose(); _lifetime.Dispose(); _allowClose = true;
        await Dispatcher.InvokeAsync(Close, DispatcherPriority.Background);
    }
}
