using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;
using Clipboard = System.Windows.Clipboard;
using MessageBox = System.Windows.MessageBox;

namespace AIHub;

public partial class MainWindow
{
    private readonly ScreenCaptureSettingsControl _captureSettings = new();
    private CaptureHotkeys? _captureHotkeys;
    private readonly CancellationTokenSource _captureLifetime = new();
    private bool _capturing;
    private CaptureHotkeyWindow? _captureBindingWindow;
    private CaptureAreaWindow? _captureAreaWindow;
    private BitmapSource? _unsavedCapture;
    private BitmapSource? _unsavedScaled;
    private CaptureSource _unsavedSource;

    private void InitializeScreenCapture()
    {
        _appSettings.ScreenCapture.Normalize();
        CapturePage.Changed += () => SaveCaptureSettings(true);
        CapturePage.AssignRequested += AssignCaptureHotkey;
        CapturePage.ErrorReported += error => ReportCaptureError(error);
        CapturePage.FolderRequested += OpenCaptureFolder;
        CapturePage.StopRequested += StopCaptureRecording;
        CapturePage.RecoveryRequested += RecoverGif;
        CapturePage.VideoRecoveryRequested += RecoverVideo;
        _captureSettings.Changed += () => SaveCaptureSettings(false);
        _captureSettings.FolderRequested += OpenCaptureFolder;
        _captureSettings.ErrorReported += error => ReportCaptureError(error);
        _captureSettings.AssignRequested += AssignCaptureHotkey;
        RefreshCaptureLocalization();
        SourceInitialized += (_, _) =>
        {
            try
            {
                _captureHotkeys = new(new WindowInteropHelper(this).Handle);
                _captureHotkeys.Command += command => Dispatcher.BeginInvoke(() =>
                {
                    if (_captureBindingWindow is not null) return;
                    if (command == "Stop") { StopCaptureRecording(); return; }
                    var parts = command.Split('.');
                    if (parts.Length == 2 && parts[0] == nameof(CaptureMode.Screenshot) && Enum.TryParse<CaptureSource>(parts[1], out var source))
                        _ = TakeScreenshotAsync(source);
                    else if (parts.Length == 2 && parts[0] == nameof(CaptureMode.Gif) && Enum.TryParse<CaptureSource>(parts[1], out var gifSource) && gifSource != CaptureSource.Desktop)
                        StartGif(gifSource);
                    else if (parts.Length == 2 && parts[0] == nameof(CaptureMode.Video) && Enum.TryParse<CaptureSource>(parts[1], out var videoSource))
                        StartVideo(videoSource);
                });
                _captureHotkeys.Configure(_appSettings.ScreenCapture.Hotkeys); RefreshCaptureLocalization();
            }
            catch (Exception error) { ReportCaptureError(error); }
        };
        Closed += (_, _) =>
        {
            _captureLifetime.Cancel(); _captureHotkeys?.Dispose();
            _gifRecording?.CancelAssembly(); _gifIndicator?.Finish();
            _videoRecording?.CancelAssembly(); _videoIndicator?.Finish();
            _captureBindingWindow?.Close(); _captureAreaWindow?.Close();
        };
    }

    private void RefreshCaptureLocalization()
    {
        CapturePage.Configure(_appSettings.ScreenCapture, L, CaptureBindingStatus);
        _captureSettings.Configure(_appSettings.ScreenCapture, L, CaptureBindingStatus);
    }
    private string CaptureBindingStatus(string command) =>
        _captureHotkeys?.Conflicts.TryGetValue(command, out var reason) == true
            ? L(reason == "duplicate" ? "Capture.ConflictDuplicate" : "Capture.ConflictSystem") : "";
    private void SaveCaptureSettings(bool fromUtility)
    {
        try
        {
            _appSettingsStore.Save(_appSettings); _captureHotkeys?.Configure(_appSettings.ScreenCapture.Hotkeys);
            // Refresh the other presentation only; don't replace the editor the user is typing in.
            if (fromUtility) _captureSettings.Refresh(); else CapturePage.Refresh();
        }
        catch (Exception error) { ReportCaptureError(error); }
    }
    private void AssignCaptureHotkey(string command)
    {
        if (_captureHotkeys is null || _captureBindingWindow is not null) return;
        _captureBindingWindow = new(this, _captureHotkeys, L);
        try
        {
            if (_captureBindingWindow.ShowDialog() == true)
            { _appSettings.ScreenCapture.Hotkeys[command] = _captureBindingWindow.Selection; SaveCaptureSettings(true); }
        }
        finally { _captureBindingWindow = null; _captureSettings.Refresh(); CapturePage.Refresh(); }
    }
    private void OpenCaptureScenario()
    {
        FinancialPage.Visibility = Visibility.Collapsed;
        if (ScenarioNavigationPage.IsHome) ScenarioNavigationPage.SelectDirection(ScenarioNavigationCatalog.Utilities);
        ScenarioNavigationPage.Visibility = Visibility.Collapsed; CapturePage.Visibility = Visibility.Visible;
        StatusText.Text = "";
    }
    private void OpenCaptureFolder()
    {
        try
        {
            var folder = ScreenshotFiles.Folder(_appSettings.ScreenCapture); System.IO.Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception error) { ReportCaptureError(error); }
    }

    private async Task TakeScreenshotAsync(CaptureSource source)
    {
        if (_capturing || _gifTask is not null || _videoTask is not null || _captureBindingWindow is not null || Dispatcher.HasShutdownStarted) return;
        if (_unsavedCapture is not null) { ReportCaptureError(new InvalidOperationException(L("Capture.PendingFrame"))); return; }
        // Freeze choices for this shot; settings can be edited while native capture is awaited.
        var settings = System.Text.Json.JsonSerializer.Deserialize<ScreenCaptureSettings>(
            System.Text.Json.JsonSerializer.Serialize(_appSettings.ScreenCapture))!;
        var output = settings.Outputs[source];
        if (!output.Clipboard && !output.File) { NotifyCapture(L("Capture.NoOutput"), true); return; }
        _capturing = true;
        var wasVisible = IsVisible; var state = WindowState; var active = IsActive;
        IReadOnlyList<string> saved = [];
        try
        {
            var token = _captureLifetime.Token;
            var selectedDisplay = source == CaptureSource.Monitor ? CaptureDisplayGeometry.AtCursor() : null;
            await ComponentLicenseGate.EnsureAsync("nuget.SkiaSharp", token);
            var capture = new ScreenFrameCapture(); BitmapSource original;
            if (source == CaptureSource.Window)
            {
                if (!IsVisible || WindowState == WindowState.Minimized)
                {
                    if (!settings.RestoreHiddenWindow) { NotifyCapture(L("Capture.HiddenUnavailable"), true); return; }
                    Show(); WindowState = wasVisible && state != WindowState.Minimized ? state : _lastNonMinimizedWindowState;
                    await Task.Delay(180, token);
                }
                original = await capture.CaptureAsync(new WindowInteropHelper(this).Handle, true, settings.IncludeCursor, token);
                // Restore visibility before any clipboard question is displayed.
                if (!wasVisible) Hide(); else WindowState = state;
            }
            else if (source == CaptureSource.Monitor)
            {
                var display = selectedDisplay!;
                original = await capture.CaptureAsync(display.Handle, false, settings.IncludeCursor, token);
            }
            else
            {
                var displays = CaptureDisplayGeometry.Displays(); var bounds = CaptureDisplayGeometry.Union(displays);
                if ((long)bounds.Width * bounds.Height > 64_000_000) throw new InvalidOperationException(L("Capture.TooLarge"));
                var pixels = new byte[checked(bounds.Width * bounds.Height * 4)];
                foreach (var display in displays)
                {
                    var image = await capture.CaptureAsync(display.Handle, false, settings.IncludeCursor, token);
                    if (image.PixelWidth != display.Bounds.Width || image.PixelHeight != display.Bounds.Height)
                        throw new InvalidOperationException(L("Capture.DisplayChanged"));
                    var buffer = ScreenshotFiles.Pixels(image);
                    for (var row = 0; row < image.PixelHeight; row++) Buffer.BlockCopy(buffer, row * image.PixelWidth * 4, pixels,
                        ((display.Bounds.Y - bounds.Y + row) * bounds.Width + display.Bounds.X - bounds.X) * 4, image.PixelWidth * 4);
                }
                original = BitmapSource.Create(bounds.Width, bounds.Height, 96, 96, PixelFormats.Pbgra32, null, pixels, bounds.Width * 4);
                original.Freeze();
                if (source == CaptureSource.Area)
                {
                    _captureAreaWindow = new(original, bounds, L("Capture.AreaHint"));
                    try
                    {
                        var accepted = _captureAreaWindow.ShowDialog();
                        if (_captureAreaWindow.Failure is { } failure) throw failure;
                        if (accepted != true || _captureAreaWindow.SelectedArea is not { } area) return;
                        original = new CroppedBitmap(original, area); original.Freeze();
                    }
                    finally { _captureAreaWindow = null; }
                }
            }
            _unsavedCapture = original; _unsavedScaled = null; _unsavedSource = source;
            var scaled = source == CaptureSource.Area && settings.EnlargeArea
                ? await Task.Run(() => ScreenshotFiles.Enlarge(original), token) : null;
            _unsavedScaled = scaled;
            var delivery = new ScreenshotDelivery(() => Clipboard.ContainsText(), CaptureClipboardSequence,
                image => Clipboard.SetImage(image), () => MessageBox.Show(L("Capture.ReplaceText"), L("Capture.Title"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);
            var result = await delivery.DeliverAsync(settings, source, original, scaled, token); saved = result.Files;
            _unsavedCapture = null; _unsavedScaled = null;
            NotifyCapture(L(result.TextKept ? "Capture.TextKept" : "Capture.Done") + (saved.Count > 0 ? "\n" + string.Join("\n", saved) : ""));
        }
        catch (OperationCanceledException) { }
        catch (ScreenshotDeliveryException error) { ReportCaptureError(error, error.Files); }
        catch (Exception error) { ReportCaptureError(error, saved); }
        finally
        {
            if (!wasVisible && IsVisible && source == CaptureSource.Window) Hide();
            else if (wasVisible && source == CaptureSource.Window) { WindowState = state; if (active) Activate(); }
            _capturing = false;
        }
    }
    private void NotifyCapture(string message, bool warning = false)
    {
        CapturePage.SetStatus(message);
        if (_applicationTray?.Notify(L("Capture.Title"), message, warning) != true)
            MessageBox.Show(message, L("Capture.Title"), MessageBoxButton.OK, warning ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }
    private void ReportCaptureError(Exception error, IReadOnlyList<string>? saved = null)
    {
        var message = L("Capture.Error") + "\n" + error.Message;
        if (saved?.Count > 0) message += "\n" + string.Join("\n", saved);
        NotifyCapture(message, true);
        if (_unsavedCapture is null) return;
        // Keep a bounded last frame after disk/clipboard failure; offer a different destination.
        if (MessageBox.Show(L("Capture.RetrySave"), L("Capture.Title"), MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        using var folder = new System.Windows.Forms.FolderBrowserDialog();
        if (folder.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        try
        {
            var recovery = new ScreenCaptureSettings { Folder = folder.SelectedPath };
            var paths = ScreenshotFiles.Save(recovery, _unsavedSource, _unsavedCapture, _unsavedScaled);
            _unsavedCapture = null; _unsavedScaled = null; NotifyCapture(L("Capture.Done") + "\n" + string.Join("\n", paths));
        }
        catch (Exception saveError) { NotifyCapture(L("Capture.Error") + "\n" + saveError.Message, true); }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetClipboardSequenceNumber")]
    private static extern uint CaptureClipboardSequence();
}
