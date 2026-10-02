using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;

namespace AIHub;

public partial class MainWindow
{
    private VideoRecordingSession? _videoRecording;
    private Task? _videoTask;
    private CaptureSource? _videoSource;
    private GifRecordingWindow? _videoIndicator;
    private string? _videoRecoveryDirectory;
    private bool _videoRecovering;
    private void StopCaptureRecording() { StopGif(); _videoRecording?.Stop(); }
    private async void StartVideo(CaptureSource source)
    {
        if (_videoTask is not null)
        {
            if (_videoSource == source && _videoRecording is { Assembling: false }) _videoRecording.Stop();
            return;
        }
        if (_gifTask is not null || _gifRecovering || _videoRecovering || _capturing || _captureBindingWindow is not null || _processShutdownPending) return;
        _videoSource = source; _videoTask = RunVideoAsync(source);
        try { await _videoTask; } finally { _videoTask = null; _videoSource = null; }
    }
    private async Task RunVideoAsync(CaptureSource source)
    {
        var wasVisible = IsVisible; var state = WindowState; var active = IsActive;
        var settings = System.Text.Json.JsonSerializer.Deserialize<ScreenCaptureSettings>(System.Text.Json.JsonSerializer.Serialize(_appSettings.ScreenCapture))!;
        try
        {
            var token = _captureLifetime.Token;
            var display = source == CaptureSource.Monitor ? CaptureDisplayGeometry.AtCursor() : null;
            var licenses = new List<string> { "nuget.SkiaSharp", "capture.ffmpeg" };
            if (settings.AudioMode != "off") { licenses.Add("nuget.NAudio.Wasapi"); licenses.Add("nuget.NAudio.Core"); }
            await ComponentLicenseGate.EnsureAsync(licenses, token);
            if (!System.IO.File.Exists(VideoEncoder.Executable)) throw new System.IO.IOException(L("Capture.VideoMissing"));
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
                var desktop = CaptureDisplayGeometry.Union(CaptureDisplayGeometry.Displays()); bounds = desktop;
                if (source == CaptureSource.Area)
                {
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
                }
            }
            _videoRecording = new(settings, source); _videoRecoveryDirectory = _videoRecording.CacheDirectory;
            if (settings.VideoShowControls)
            {
                _videoIndicator = new(this, L, StopCaptureRecording, () => _videoRecording?.CancelAssembly(), video: true, settings.AudioMode);
                _videoIndicator.Show(); if (_videoIndicator.Failure is { } failure) throw failure;
            }
            _applicationTray?.SetCaptureRecording(true); var lastUpdate = DateTime.MinValue;
            _videoRecording.Progress += progress =>
            {
                if (DateTime.UtcNow - lastUpdate < TimeSpan.FromMilliseconds(200) && progress.Percent != 100) return;
                lastUpdate = DateTime.UtcNow;
                Dispatcher.BeginInvoke(() =>
                {
                    _videoIndicator?.UpdateVideo(progress);
                    CapturePage.SetStatus(progress.Assembling ? string.Format(L("Capture.VideoAssembling"), progress.Percent)
                        : string.Format(L("Capture.VideoRecording"), progress.Elapsed.ToString(@"hh\:mm\:ss"), progress.Width, progress.Height));
                });
            };
            var result = await _videoRecording.RunAsync(target, source == CaptureSource.Window, bounds, token);
            _videoIndicator?.Finish(); _videoIndicator = null; _applicationTray?.SetCaptureRecording(false);
            if (result.File is { } path)
            {
                _videoRecoveryDirectory = null; var message = L("Capture.Done") + "\n" + path;
                if (result.ReducedDetail) message += "\n" + L("Capture.GifReduced");
                if (result.MissedFrames > 0) message += "\n" + string.Format(L("Capture.GifMissed"), result.MissedFrames);
                if (result.Error.Length > 0) message += "\n" + result.Error;
                if (result.EncodingFallback.Length > 0) message += "\n" + L("Capture.VideoEncoderFallback");
                NotifyCapture(message, result.Error.Length > 0 || result.MissedFrames > 0);
            }
            else NotifyCapture(L("Capture.VideoPreserved") + "\n" + result.CacheDirectory + "\n" + result.Error, true);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (_videoRecording is not null)
                NotifyCapture(L("Capture.VideoPreserved") + "\n" + _videoRecording.CacheDirectory + "\n" + error.Message, true);
            else ReportCaptureError(error);
        }
        finally
        {
            _videoIndicator?.Finish(); _videoIndicator = null; _videoRecording = null; _applicationTray?.SetCaptureRecording(false);
            if (source == CaptureSource.Window)
            { if (!wasVisible && IsVisible) Hide(); else if (wasVisible) { WindowState = state; if (active) Activate(); } }
        }
    }
    private async void RecoverVideo()
    {
        if (_videoTask is not null || _gifTask is not null || _gifRecovering || _videoRecovering || _capturing) return;
        using var dialog = new System.Windows.Forms.FolderBrowserDialog { InitialDirectory = _videoRecoveryDirectory ?? ScreenshotFiles.Folder(_appSettings.ScreenCapture), Description = L("Capture.VideoRecoveryFolder") };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        _videoRecovering = true;
        try
        {
            await ComponentLicenseGate.EnsureAsync(["nuget.SkiaSharp", "capture.ffmpeg"], _captureLifetime.Token);
            var path = await VideoRecordingSession.RecoverAsync(dialog.SelectedPath, _captureLifetime.Token,
                percent => Dispatcher.BeginInvoke(() => CapturePage.SetStatus(string.Format(L("Capture.VideoAssembling"), percent))));
            NotifyCapture(L("Capture.Done") + "\n" + path);
        }
        catch (Exception error) { ReportCaptureError(error); }
        finally { _videoRecovering = false; }
    }
}
