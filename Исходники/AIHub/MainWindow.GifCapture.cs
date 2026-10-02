using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;
using MessageBox = System.Windows.MessageBox;

namespace AIHub;

public partial class MainWindow
{
    private GifRecordingSession? _gifRecording;
    private Task? _gifTask;
    private CaptureSource? _gifSource;
    private GifRecordingWindow? _gifIndicator;
    private string? _gifRecoveryDirectory;
    private bool _gifRecovering;

    private async void StartGif(CaptureSource source)
    {
        if (source == CaptureSource.Desktop) return;
        if (_gifTask is not null)
        {
            // Source shortcuts toggle their own recording by default; never cancel finalization.
            if (_gifSource == source && _gifRecording is { Assembling: false }) StopGif();
            return;
        }
        if (_gifRecovering || _videoTask is not null || _videoRecovering || _capturing || _captureBindingWindow is not null || _processShutdownPending) return;
        _gifSource = source;
        _gifTask = RunGifAsync(source);
        try { await _gifTask; }
        finally { _gifTask = null; _gifSource = null; }
    }
    private void StopGif()
    {
        _gifRecording?.Stop();
    }
    private async Task RunGifAsync(CaptureSource source)
    {
        var wasVisible = IsVisible; var state = WindowState; var active = IsActive;
        var settings = System.Text.Json.JsonSerializer.Deserialize<ScreenCaptureSettings>(System.Text.Json.JsonSerializer.Serialize(_appSettings.ScreenCapture))!;
        try
        {
            var token = _captureLifetime.Token;
            var display = source == CaptureSource.Monitor ? CaptureDisplayGeometry.AtCursor() : null;
            await ComponentLicenseGate.EnsureAsync("nuget.SkiaSharp", token);
            Int32Rect bounds = default; nint target = 0;
            if (source == CaptureSource.Window)
            {
                if (!IsVisible || WindowState == WindowState.Minimized)
                {
                    if (!settings.RestoreHiddenWindow) { NotifyCapture(L("Capture.HiddenUnavailable"), true); return; }
                    Show(); WindowState = _lastNonMinimizedWindowState; await Task.Delay(180, token);
                }
                target = new WindowInteropHelper(this).Handle;
            }
            else if (source == CaptureSource.Monitor) { bounds = display!.Bounds; target = display.Handle; }
            else
            {
                var desktop = CaptureDisplayGeometry.Union(CaptureDisplayGeometry.Displays());
                var preview = await Task.Run(() => GifDesktopFrames.Grab(desktop, 100, false), token);
                var image = BitmapSource.Create(preview.Width, preview.Height, 96, 96, PixelFormats.Pbgra32, null, preview.Bgra, preview.Width * 4); image.Freeze();
                _captureAreaWindow = new(image, desktop, L("Capture.AreaHint"));
                try
                {
                    if (_captureAreaWindow.ShowDialog() != true || _captureAreaWindow.SelectedArea is not { } area) return;
                    if (_captureAreaWindow.Failure is { } failure) throw failure;
                    bounds = new(desktop.X + area.X, desktop.Y + area.Y, area.Width, area.Height);
                }
                finally { _captureAreaWindow = null; }
                // A rectangle may span displays; CPU collection samples the physical desktop directly.
                settings.Processing = "cpu";
            }
            _gifRecording = new(settings, source); _gifRecoveryDirectory = _gifRecording.CacheDirectory;
            if (settings.GifShowControls)
            {
                _gifIndicator = new(this, L, StopGif, () => _gifRecording?.CancelAssembly());
                _gifIndicator.Show();
                if (_gifIndicator.Failure is { } indicatorError) throw indicatorError;
            }
            _applicationTray?.SetCaptureRecording(true);
            var lastUpdate = DateTime.MinValue;
            _gifRecording.Progress += progress =>
            {
                var now = DateTime.UtcNow;
                if (now - lastUpdate < TimeSpan.FromMilliseconds(200) && progress.Percent != 100) return;
                lastUpdate = now;
                Dispatcher.BeginInvoke(() =>
                {
                    _gifIndicator?.Update(progress);
                    CapturePage.SetStatus(progress.Assembling ? string.Format(L("Capture.GifAssembling"), progress.Percent)
                        : string.Format(L("Capture.GifRecording"), progress.Elapsed.ToString(@"mm\:ss"), progress.Width, progress.Height));
                });
            };
            var result = await _gifRecording.RunAsync(target, source == CaptureSource.Window, bounds, token);
            _gifIndicator?.Finish(); _gifIndicator = null; _applicationTray?.SetCaptureRecording(false);
            if (result.File is { } path)
            {
                _gifRecoveryDirectory = null;
                var message = L("Capture.Done") + "\n" + path;
                if (result.ReducedDetail) message += "\n" + L("Capture.GifReduced");
                if (result.MissedFrames > 0) message += "\n" + string.Format(L("Capture.GifMissed"), result.MissedFrames);
                if (result.Error.Length > 0) message += "\n" + result.Error;
                NotifyCapture(message, result.Error.Length > 0 || result.MissedFrames > 0);
            }
            else NotifyCapture(L("Capture.GifPreserved") + "\n" + result.CacheDirectory + "\n" + result.Error, true);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ReportCaptureError(error); }
        finally
        {
            _gifIndicator?.Finish(); _gifIndicator = null; _gifRecording = null; _applicationTray?.SetCaptureRecording(false);
            if (source == CaptureSource.Window)
            {
                if (!wasVisible && IsVisible) Hide();
                else if (wasVisible) { WindowState = state; if (active) Activate(); }
            }
        }
    }
    private async void RecoverGif()
    {
        if (_gifTask is not null || _videoTask is not null || _videoRecovering || _gifRecovering || _capturing) return;
        using var dialog = new System.Windows.Forms.FolderBrowserDialog { InitialDirectory = _gifRecoveryDirectory ?? ScreenshotFiles.Folder(_appSettings.ScreenCapture), Description = L("Capture.GifRecoveryFolder") };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        _gifRecovering = true;
        try
        {
            await ComponentLicenseGate.EnsureAsync("nuget.SkiaSharp", _captureLifetime.Token);
            var result = await GifRecordingSession.RecoverAsync(dialog.SelectedPath, _captureLifetime.Token,
                percent => Dispatcher.BeginInvoke(() => CapturePage.SetStatus(string.Format(L("Capture.GifAssembling"), percent))));
            NotifyCapture(L("Capture.Done") + "\n" + result);
        }
        catch (Exception error) { ReportCaptureError(error); }
        finally { _gifRecovering = false; }
    }
}
