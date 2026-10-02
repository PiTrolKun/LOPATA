using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIHub.Models;
using AIHub.Services;
using AIHub.Controls;
using SkiaSharp;
using Application = System.Windows.Application;
using Window = System.Windows.Window;
using Color = System.Windows.Media.Color;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using CheckBox = System.Windows.Controls.CheckBox;
using Brushes = System.Windows.Media.Brushes;

internal static class Program
{
    private static int _checks;
    private static readonly List<string> Results = [];
    [STAThread] private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; var result = 1;
        var run = Path.GetFullPath("Тесты/ScreenCapture/runs/" + DateTime.Now.ToString("yyyyMMdd_HHmmss")); Directory.CreateDirectory(run);
        app.Dispatcher.BeginInvoke(async () =>
        {
            try { await Run(run); result = 0; Console.WriteLine($"PASS {_checks}: {run}"); }
            catch (Exception error) { Console.WriteLine(error); File.WriteAllText(Path.Combine(run, "failure.txt"), error.ToString()); }
            finally { File.WriteAllLines(Path.Combine(run, "checks.txt"), Results); app.Shutdown(); }
        });
        app.Run(); return result;
    }
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); _checks++; Results.Add(name); }
    private static async Task Run(string run)
    {
        var window = new Window { Width = 600, Height = 400, ShowInTaskbar = false, Content = new TextBlock { Text = "LOPATA capture test", FontSize = 32, Background = Brushes.DarkBlue, Foreground = Brushes.White } };
        window.Show(); await Task.Delay(180);
        try
        {
            var frame = new ScreenFrameCapture(); var image = await frame.CaptureAsync(new WindowInteropHelper(window).Handle, true, false, CancellationToken.None);
            Check(image.PixelWidth > 300 && image.PixelHeight > 200, "WGC window produces native pixels");
            var pixels = ScreenshotFiles.Pixels(image); var blue = 0;
            for (var i = 0; i < pixels.Length; i += 4) if (pixels[i] > 100 && pixels[i + 1] < 40 && pixels[i + 2] < 40) blue++;
            Check(blue > image.PixelWidth * image.PixelHeight / 2, "Frame contains actual blue window content, not a blank frame");
            var softwareImage = await frame.CaptureAsync(new WindowInteropHelper(window).Handle, true, false, default, true);
            Check(softwareImage.PixelWidth == image.PixelWidth && softwareImage.PixelHeight == image.PixelHeight, "Software D3D device produces native-size frame");
            foreach (var format in new[] { "png", "jpeg", "webp" }) foreach (var lossless in new[] { true, false })
            {
                var settings = new ScreenCaptureSettings { Folder = run, ImageFormat = format, WebPLossless = lossless };
                var paths = ScreenshotFiles.Save(settings, CaptureSource.Window, image, null);
                Check(paths.Count == 1 && File.Exists(paths[0]), format + " output exists");
                using var decoded = SKBitmap.Decode(paths[0]);
                Check(decoded is not null && decoded.Width == image.PixelWidth && decoded.Height == image.PixelHeight, format + " decodes at original size");
            }
            var tiny = new CroppedBitmap(image, new(20, 60, 20, 30)); tiny.Freeze(); var enlarged = ScreenshotFiles.Enlarge(tiny);
            Check(enlarged.PixelWidth >= 100 && enlarged.PixelHeight >= 100, "Tiny crop meets minimum dimensions");
            Check(Math.Abs((double)enlarged.PixelWidth / enlarged.PixelHeight - 2d / 3) < .01, "Crop aspect ratio preserved");
            var files = ScreenshotFiles.Save(new() { Folder = run }, CaptureSource.Area, tiny, enlarged);
            Check(files.Count == 2 && files[0] != files[1], "Original and enlarged images saved separately");
            var duplicate = ScreenshotFiles.Save(new() { Folder = run }, CaptureSource.Area, tiny, enlarged);
            Check(!duplicate.Intersect(files).Any(), "Repeated saves never overwrite");
            await DeliveryChecks(run, tiny, enlarged);
            var displays = CaptureDisplayGeometry.Displays(); Check(displays.Count > 0, "Physical monitor enumeration");
            foreach (var display in displays)
            {
                var screen = await frame.CaptureAsync(display.Handle, false, false, CancellationToken.None);
                Check(screen.PixelWidth == display.Bounds.Width && screen.PixelHeight == display.Bounds.Height, "Native monitor geometry " + display.Bounds);
                // Desktop pixels remain in RAM and are not saved to public test artifacts.
            }
            await AreaChecks(CaptureDisplayGeometry.Union(displays));
            var union = CaptureDisplayGeometry.Union([new(1, new(-1920, 0, 1920, 1080)), new(2, new(0, -200, 2560, 1440))]);
            Check(union == new Int32Rect(-1920, -200, 4480, 1440), "Negative and offset monitor union");
            var invalid = new ScreenCaptureSettings { GifSeconds = 200, ImageQuality = -10, Outputs = null!, Hotkeys = null! }; invalid.Normalize();
            Check(invalid.GifSeconds == 60 && invalid.ImageQuality == 1 && invalid.Outputs.Count == 4, "Settings normalization");
            var json = JsonSerializer.Serialize(invalid); var loaded = JsonSerializer.Deserialize<ScreenCaptureSettings>(json)!;
            Check(loaded.Outputs.Count == 4 && loaded.Hotkeys.Count == 4, "Settings round trip");
            foreach (var language in new[] { "ru", "en" })
            {
                var texts = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText($"Исходники/AIHub/Localization/{language}.json"))!;
                string L(string key) => texts.GetValueOrDefault(key, key);
                foreach (var dark in new[] { true, false })
                {
                    window.Resources["WindowBackgroundBrush"] = new SolidColorBrush(dark ? Color.FromRgb(17, 24, 39) : Colors.White);
                    window.Resources["TextPrimaryBrush"] = dark ? Brushes.White : Brushes.Black;
                    window.Resources["TextSecondaryBrush"] = dark ? Brushes.LightGray : Brushes.DimGray;
                    window.Resources["SecondaryButtonBackgroundBrush"] = dark ? new SolidColorBrush(Color.FromRgb(17, 24, 39)) : Brushes.WhiteSmoke;
                    window.Resources["LineBrush"] = Brushes.SlateGray; window.Resources["AccentBrush"] = Brushes.RoyalBlue;
                    window.Resources["UiBodyFontSize"] = 18d; window.FontSize = 18;
                    window.Resources["PanelBrush"] = dark ? new SolidColorBrush(Color.FromRgb(23, 32, 50)) : Brushes.WhiteSmoke;
                    window.Background = (System.Windows.Media.Brush)window.Resources["WindowBackgroundBrush"];
                    window.Width = 1280; window.Height = 1000;
                    var shared = new ScreenCaptureSettings();
                    var page = new ScreenCaptureControl(); page.Configure(shared, L, _ => ""); window.Content = page; window.UpdateLayout();
                    Shot(page, run, language + (dark ? "-dark" : "-light"));
                    Check(!FindButtons(page).Any(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "Capture.Take"), "Manual shot button removed " + language + dark);
                    Check(FindButtons(page).Count(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b).StartsWith("Capture.Binding.")) == 4, "Four screenshot actions have independent assignments " + language + dark);
                    var format = Descendants<ComboBox>(page).Single(c => Id(c) == "Capture.ImageFormat"); format.SelectedIndex = 1;
                    Check(shared.ImageFormat == "jpeg", "Utility editor changes shared image format " + language + dark);
                    FindButtons(page).First(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "Capture.Mode.Gif").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(FindButtons(page).Count(b => Id(b).StartsWith("Capture.Binding.")) == 4, "GIF has three source assignments and stop " + language + dark);
                    Check(!FindButtons(page).Any(b => Id(b) == "Capture.Take"), "GIF launches through source hotkeys " + language + dark);
                    Check(Descendants<ComboBox>(page).Any(c => Id(c) == "Capture.GifSeconds"), "GIF parameters inside utility " + language + dark);
                    var controls = Descendants<CheckBox>(page).Single(c => Id(c) == "Capture.GifShowControls");
                    Check(controls.IsChecked == true, "GIF controls window remains enabled by default " + language + dark);
                    controls.IsChecked = false;
                    Check(!shared.GifShowControls, "Utility can disable GIF controls window " + language + dark);
                    FindButtons(page).First(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "Capture.Mode.Video").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(!FindButtons(page).Any(b => Id(b) == "Capture.Take"), "Video uses source shortcuts " + language + dark);
                    Check(Descendants<CheckBox>(page).Single(c => Id(c) == "Capture.VideoShowControls").IsChecked == true, "Video controls enabled by default " + language + dark);
                    var devices = Descendants<ComboBox>(page).Single(c => Id(c) == "Capture.Playback");
                    Check(devices.Items.Count > 1 && !devices.IsEnabled, "Actual devices listed without enabling sound " + language + dark);
                    var audioMode = Descendants<ComboBox>(page).Single(c => Id(c) == "Capture.Audio"); audioMode.SelectedIndex = 3;
                    Check(devices.IsEnabled && Descendants<ComboBox>(page).Single(c => Id(c) == "Capture.Microphone").IsEnabled, "Both-mode enables both device choices " + language + dark);
                    window.UpdateLayout(); await Task.Delay(100);
                    Shot(page, run, language + (dark ? "-video-dark" : "-video-light"));
                    Check(FindButtons(page).Count(b => Id(b).StartsWith("Capture.Binding.")) == 5, "Video assignments inside utility " + language + dark);
                    Check(Descendants<ComboBox>(page).Any(c => Id(c) == "Capture.Audio") && Descendants<ComboBox>(page).Any(c => Id(c) == "Capture.VideoFormat"), "Video and sound parameters inside utility " + language + dark);
                    window.Width = 560; window.UpdateLayout();
                    Check(Descendants<System.Windows.Controls.Primitives.UniformGrid>(page).Single().Columns == 1, "Narrow utility stacks action cards " + language + dark);
                    window.Width = 1280; window.UpdateLayout();
                    var settingsUi = new ScreenCaptureSettingsControl(); settingsUi.Configure(new(), L, _ => ""); window.Content = settingsUi; window.UpdateLayout();
                    Check(Descendants<CheckBox>(settingsUi).Any(c => Id(c) == "Capture.GifShowControls"), "Global settings expose GIF controls toggle " + language + dark);
                    Check(FindButtons(settingsUi).Count(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b).StartsWith("Capture.Binding.")) == 12, "Mode/source hotkeys exclude GIF desktop " + language + dark);
                    await DialogChecks(window, L, run, language + (dark ? "-dark" : "-light"));
                }
                foreach (var node in ScenarioNavigationCatalog.Nodes.Where(n => n.Id == ScenarioNavigationCatalog.Capture))
                    Check(texts.ContainsKey(node.TitleKey) && texts.ContainsKey(node.DescriptionKey), "Capture navigation localized " + language);
                foreach (var tag in ScenarioNavigationCatalog.CloudTags)
                    Check(texts.ContainsKey(tag.TitleKey) && texts.ContainsKey(tag.DescriptionKey), "Cloud localized " + language + " " + tag.Id);
            }
            using var hotkeys = new CaptureHotkeys(new WindowInteropHelper(window).Handle);
            hotkeys.Configure(new() { ["one"] = [0x11, 0x12, 0x10, 0x7A], ["two"] = [0x11, 0x12, 0x10, 0x7A] });
            Check(hotkeys.Conflicts.Count == 2, "Duplicate chords disabled");
            hotkeys.Configure(new() { ["one"] = [0x11, 0x12, 0x10, 0x7A] });
            Check(hotkeys.Conflicts.Count == 0, "Four-key hotkey registered");
            var count = 0; hotkeys.Command += _ => count++;
            window.Activate();
            foreach (var key in new byte[] { 0x11, 0x12, 0x10, 0x7A }) keybd_event(key, 0, 0, 0);
            await Task.Delay(250); Check(count == 1, "Global four-key chord fires once (received " + count + "; held " + string.Join(",", new[] { 0x11, 0x12, 0x10, 0x7A }.Select(k => GetAsyncKeyState(k))) + ")");
            await Task.Delay(160); Check(count == 1, "Held keys do not repeat");
            foreach (var key in new byte[] { 0x7A, 0x10, 0x12, 0x11 }) keybd_event(key, 0, 2, 0);
            await Task.Delay(100);
            hotkeys.Configure(new() { ["complex"] = [0x11, 0x7C, 0x7D] }); count = 0;
            foreach (var key in new byte[] { 0x11, 0x7C, 0x7D }) keybd_event(key, 0, 0, 0);
            await Task.Delay(180); Check(count == 1, "Chord with two ordinary additional keyboard keys works");
            foreach (var key in new byte[] { 0x7D, 0x7C, 0x11 }) keybd_event(key, 0, 2, 0);
            await Task.Delay(100);
            hotkeys.Configure(new() { ["mixed"] = [0x11, 5] }); count = 0;
            keybd_event(0x11, 0, 0, 0); mouse_event(0x80, 0, 0, 1, 0);
            await Task.Delay(180); Check(count == 1, "Ctrl plus extra mouse X1 works");
            await Task.Delay(100); Check(count == 1, "Held mixed chord does not repeat");
            mouse_event(0x100, 0, 0, 1, 0); keybd_event(0x11, 0, 2, 0); await Task.Delay(100);
            hotkeys.BeginRecording(); int[] held = []; hotkeys.HeldChanged += keys => held = keys;
            keybd_event(0x11, 0, 0, 0); keybd_event(0x7C, 0, 0, 0); await Task.Delay(180);
            Check(held.Contains(0x11) && held.Contains(0x7C), "Recorder reports currently held additional keys live");
            keybd_event(0x7C, 0, 2, 0); keybd_event(0x11, 0, 2, 0); await Task.Delay(100);
            Check(held.Length == 0, "Recorder clears released keys"); hotkeys.EndRecording();
            hotkeys.Configure(new() { ["reserved"] = [0x11, 0x12, 0x10, 0x7A] });
            var otherWindow = new Window { Width = 180, Height = 100, ShowInTaskbar = false }; otherWindow.Show(); otherWindow.Activate();
            try
            {
                using var collision = new CaptureHotkeys(new WindowInteropHelper(otherWindow).Handle);
                collision.Configure(new() { ["collision"] = [0x11, 0x12, 0x10, 0x7A] });
                Check(collision.Conflicts.GetValueOrDefault("collision") == "system", "OS hotkey collision is reported and disabled");
                count = 0;
                foreach (var key in new byte[] { 0x11, 0x12, 0x10, 0x7A }) keybd_event(key, 0, 0, 0);
                await Task.Delay(180); Check(count == 1, "Global command works while another window has focus");
                foreach (var key in new byte[] { 0x7A, 0x10, 0x12, 0x11 }) keybd_event(key, 0, 2, 0);
            }
            finally { otherWindow.Close(); }
#if UPDATE_STAND
            await HostChecks(run);
#endif
        }
        finally
        {
            foreach (var key in new byte[] { 0x7A, 0x7C, 0x7D, 0x10, 0x12, 0x11 }) keybd_event(key, 0, 2, 0);
            mouse_event(0x100, 0, 0, 1, 0);
            window.Close();
        }
    }
    private static async Task AreaChecks(Int32Rect bounds)
    {
        var pixels = new byte[checked(bounds.Width * bounds.Height * 4)];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 139; pixels[i + 3] = 255; }
        var image = BitmapSource.Create(bounds.Width, bounds.Height, 96, 96, PixelFormats.Pbgra32, null, pixels, bounds.Width * 4); image.Freeze();
        var visible = CaptureDisplayGeometry.Displays()[0].Bounds;
        var physicalPointer = new System.Windows.Point(visible.X + 120, visible.Y + 160);
        var area = new CaptureAreaWindow(image, bounds, "Synthetic selection test", () => physicalPointer); Exception? failure = null;
        _ = area.Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                await Task.Delay(150);
                var previousDpi = SetThreadDpiAwarenessContext(new(-4)); Rect actual;
                try { GetWindowRect(new WindowInteropHelper(area).Handle, out actual); }
                finally { if (previousDpi != 0) SetThreadDpiAwarenessContext(previousDpi); }
                Check(actual.Left == bounds.X && actual.Top == bounds.Y && actual.Right - actual.Left == bounds.Width && actual.Bottom - actual.Top == bounds.Height,
                    "Area overlay covers physical virtual-desktop rectangle: " + actual.Left + "," + actual.Top + " " + (actual.Right - actual.Left) + "x" + (actual.Bottom - actual.Top) + " vs " + bounds);
                var hwnd = new WindowInteropHelper(area).Handle;
                nint Position(int x, int y) => new((visible.X - bounds.X + x) | ((visible.Y - bounds.Y + y) << 16));
                PostMessage(hwnd, 0x200, 0, Position(120, 160)); await Task.Delay(80);
                PostMessage(hwnd, 0x201, 1, Position(120, 160)); await Task.Delay(80);
                physicalPointer = new(visible.X + 320, visible.Y + 310);
                PostMessage(hwnd, 0x200, 1, Position(320, 310)); await Task.Delay(80);
                PostMessage(hwnd, 0x202, 0, Position(320, 310));
                await Task.Delay(250);
                if (area.IsVisible) throw new TimeoutException("Area did not accept the test drag.");
            }
            catch (Exception error) { failure = error; area.Close(); }
        });
        try
        {
            var accepted = area.ShowDialog();
            if (failure is not null) throw failure;
            Check(accepted == true, "Area drag confirms selection");
            Check(area.SelectedArea is { } rect && Math.Abs(rect.Width - 200) <= 2 && Math.Abs(rect.Height - 150) <= 2,
                "Area selection maps display coordinates to original pixels: " + area.SelectedArea);
        }
        finally { mouse_event(4, 0, 0, 0, 0); }
        await Task.CompletedTask;
    }
#if UPDATE_STAND
    private static async Task HostChecks(string run)
    {
        var host = new AIHub.MainWindow(); host.Show(); await Task.Delay(180);
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        object? Invoke(string name, params object?[] args) => typeof(AIHub.MainWindow).GetMethod(name, flags)!.Invoke(host, args);
        var settings = (AppSettings)typeof(AIHub.MainWindow).GetField("_appSettings", flags)!.GetValue(host)!;
        var page = (ScreenCaptureControl)host.FindName("CapturePage");
        settings.ScreenCapture.Folder = Path.Combine(run, "host");
        settings.ScreenCapture.Outputs[CaptureSource.Window] = new() { Clipboard = false, File = true };
        // Isolated test policy; no receipts in the user's profile are created or accepted.
        ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
        try
        {
            Invoke("ShowWorkStartPage"); Invoke("OpenCaptureScenario"); host.UpdateLayout();
            Check(page.Visibility == Visibility.Visible, "Host opens capture scenario inside work-start page");
            Invoke("ShowSettingsPage"); Invoke("BackFromSettingsButton_Click", null, new RoutedEventArgs());
            Check(page.Visibility == Visibility.Visible && ((FrameworkElement)host.FindName("WorkStartPage")).Visibility == Visibility.Visible,
                "Settings Back restores capture page");
            var global = (ScreenCaptureSettingsControl)typeof(AIHub.MainWindow).GetField("_captureSettings", flags)!.GetValue(host)!;
            Descendants<ComboBox>(page).Single(c => Id(c) == "Capture.ImageFormat").SelectedIndex = 1;
            Invoke("ShowSettingsPage"); ((SettingsWorkspace)host.FindName("SettingsNavigator")).SelectSection("utilities"); host.UpdateLayout();
            Check(CaptureUiOptionName(Descendants<ComboBox>(global).Single(c => Id(c) == "Capture.ImageFormat")) == "jpeg", "Utility change refreshes global settings");
            Descendants<ComboBox>(global).Single(c => Id(c) == "Capture.ImageFormat").SelectedIndex = 2;
            Invoke("BackFromSettingsButton_Click", null, new RoutedEventArgs()); host.UpdateLayout();
            Check(CaptureUiOptionName(Descendants<ComboBox>(page).Single(c => Id(c) == "Capture.ImageFormat")) == "webp", "Global change refreshes utility settings");
            Descendants<ComboBox>(page).Single(c => Id(c) == "Capture.ImageFormat").SelectedIndex = 0;
            var before = host.WindowState;
            await (Task)Invoke("TakeScreenshotAsync", CaptureSource.Window)!;
            Check(host.IsVisible && host.WindowState == before && page.Visibility == Visibility.Visible, "Visible window screenshot preserves page and placement");
            Check(Directory.GetFiles(settings.ScreenCapture.Folder, "*.png").Length == 1, "Host screenshot file saved");
            host.Hide(); settings.ScreenCapture.RestoreHiddenWindow = true;
            await (Task)Invoke("TakeScreenshotAsync", CaptureSource.Window)!;
            Check(!host.IsVisible && page.Visibility == Visibility.Visible, "Hidden window is temporarily restored and returns hidden with same page");
            Check(Directory.GetFiles(settings.ScreenCapture.Folder, "*.png").Length == 2, "Hidden window screenshot saved");
            settings.ScreenCapture.RestoreHiddenWindow = false;
            await (Task)Invoke("TakeScreenshotAsync", CaptureSource.Window)!;
            Check(!host.IsVisible && Directory.GetFiles(settings.ScreenCapture.Folder, "*.png").Length == 2, "Disabled hidden-window restore refuses capture without revealing window");
            settings.ScreenCapture.RestoreHiddenWindow = true;
            settings.ScreenCapture.GifSeconds = 5; settings.ScreenCapture.GifScalePercent = 25;
            settings.ScreenCapture.GifShowControls = true; Invoke("RefreshCaptureLocalization");
            Invoke("StartGif", CaptureSource.Window);
            var gifTask = (Task?)typeof(AIHub.MainWindow).GetField("_gifTask", flags)!.GetValue(host);
            Check(gifTask is not null, "Host starts GIF from hidden window");
            await Task.Delay(1500);
            Check(typeof(AIHub.MainWindow).GetField("_gifIndicator", flags)!.GetValue(host) is GifRecordingWindow { IsVisible: true },
                "Default GIF recording shows controls window");
            Invoke("StopGif"); await gifTask!;
            Check(!host.IsVisible && page.Visibility == Visibility.Visible, "GIF restores hidden placement and same page after recording");
            var gifs = Directory.GetFiles(settings.ScreenCapture.Folder, "*.gif");
            Check(gifs.Length == 1 && new GifBitmapDecoder(new Uri(gifs[0]), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames.Count > 0,
                "Host integration saves decodable GIF");
            Check(typeof(AIHub.MainWindow).GetField("_gifRecording", flags)!.GetValue(host) is null, "Host retires recording after completion");
            var controls = Descendants<CheckBox>(global).Single(c => Id(c) == "Capture.GifShowControls");
            controls.IsChecked = false;
            Check(!settings.ScreenCapture.GifShowControls, "Global GIF controls preference updates shared settings");
            // Each run starts with an isolated assignment; a previous video's F15 must not collide.
            settings.ScreenCapture.Hotkeys.Clear();
            settings.ScreenCapture.Hotkeys[ScreenCaptureSettings.Command(CaptureMode.Gif, CaptureSource.Window)] = [0x11, 0x12, 0x7E];
            settings.ScreenCapture.Hotkeys.Remove("Stop");
            var sourceKeys = (CaptureHotkeys)typeof(AIHub.MainWindow).GetField("_captureHotkeys", flags)!.GetValue(host)!;
            sourceKeys.Configure(settings.ScreenCapture.Hotkeys);
            async Task PressSourceShortcut()
            {
                keybd_event(0x11, 0, 0, 0); keybd_event(0x12, 0, 0, 0); keybd_event(0x7E, 0, 0, 0);
                await Task.Delay(120);
                keybd_event(0x7E, 0, 2, 0); keybd_event(0x12, 0, 2, 0); keybd_event(0x11, 0, 2, 0);
                await Task.Delay(120);
            }
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await PressSourceShortcut();
                var toggledTask = (Task?)typeof(AIHub.MainWindow).GetField("_gifTask", flags)!.GetValue(host);
                Check(toggledTask is not null, "Real source shortcut starts a GIF without a separate stop assignment");
                await Task.Delay(1000);
                Check(typeof(AIHub.MainWindow).GetField("_gifIndicator", flags)!.GetValue(host) is null,
                    "Disabled GIF controls window is never created during recording");
                Invoke("StartGif", CaptureSource.Monitor);
                Check(ReferenceEquals(toggledTask, typeof(AIHub.MainWindow).GetField("_gifTask", flags)!.GetValue(host)),
                    "Another source cannot replace the active GIF");
                await PressSourceShortcut(); await toggledTask!.WaitAsync(TimeSpan.FromSeconds(10));
                Check(clock.Elapsed.TotalSeconds < 4.5 && Directory.GetFiles(settings.ScreenCapture.Folder, "*.gif").Length == 2,
                    "Same real source shortcut stops early and saves the GIF");
                Check(!host.IsVisible && page.Visibility == Visibility.Visible && typeof(AIHub.MainWindow).GetField("_gifIndicator", flags)!.GetValue(host) is null,
                    "GIF without controls restores placement after toggle-stop");
            }
            finally { foreach (var key in new byte[] { 0x7E, 0x12, 0x11 }) keybd_event(key, 0, 2, 0); }
            settings.ScreenCapture.VideoQuality = "720p"; settings.ScreenCapture.VideoFps = 15; settings.ScreenCapture.Processing = "cpu";
            settings.ScreenCapture.AudioMode = "off"; settings.ScreenCapture.VideoShowControls = false;
            settings.ScreenCapture.Hotkeys.Clear(); settings.ScreenCapture.Hotkeys[ScreenCaptureSettings.Command(CaptureMode.Video, CaptureSource.Window)] = [0x11, 0x12, 0x7E];
            sourceKeys.Configure(settings.ScreenCapture.Hotkeys);
            await PressSourceShortcut();
            var videoTask = (Task?)typeof(AIHub.MainWindow).GetField("_videoTask", flags)!.GetValue(host);
            Check(videoTask is not null, "Real video source shortcut starts recording from tray");
            await Task.Delay(1400);
            Check(typeof(AIHub.MainWindow).GetField("_videoIndicator", flags)!.GetValue(host) is null, "Disabled video controls are not created");
            Invoke("StartGif", CaptureSource.Window);
            Check(typeof(AIHub.MainWindow).GetField("_gifTask", flags)!.GetValue(host) is null, "Video excludes a concurrent GIF recording");
            Invoke("StartVideo", CaptureSource.Monitor);
            Check(ReferenceEquals(videoTask, typeof(AIHub.MainWindow).GetField("_videoTask", flags)!.GetValue(host)), "Another source cannot replace the active video");
            await PressSourceShortcut(); await videoTask!.WaitAsync(TimeSpan.FromSeconds(30));
            Check(Directory.GetFiles(settings.ScreenCapture.Folder, "*.mp4").Length == 1, "Same real shortcut stops video and produces a file");
            Check(!host.IsVisible && page.Visibility == Visibility.Visible, "Video restores hidden placement and current page");
            settings.ScreenCapture.VideoShowControls = true;
            Invoke("StartVideo", CaptureSource.Window);
            videoTask = (Task?)typeof(AIHub.MainWindow).GetField("_videoTask", flags)!.GetValue(host);
            await Task.Delay(1000);
            Check(typeof(AIHub.MainWindow).GetField("_videoIndicator", flags)!.GetValue(host) is GifRecordingWindow { IsVisible: true }, "Enabled video controls are shown");
            Invoke("StopCaptureRecording"); await videoTask!.WaitAsync(TimeSpan.FromSeconds(30));
            Check(Directory.GetFiles(settings.ScreenCapture.Folder, "*.mp4").Length == 2, "Shared Stop command finishes video");
            host.Show(); Invoke("BackFromWorkStartButton_Click", null, new RoutedEventArgs());
            Check(page.Visibility == Visibility.Collapsed && ((FrameworkElement)host.FindName("ScenarioNavigationPage")).Visibility == Visibility.Visible,
                "Scenario Back returns to utility navigation");
            settings.ScreenCapture.VideoShowControls = false;
            Invoke("StartVideo", CaptureSource.Window);
            videoTask = (Task?)typeof(AIHub.MainWindow).GetField("_videoTask", flags)!.GetValue(host);
            await Task.Delay(1000);
            var closed = false; host.Closed += (_, _) => closed = true;
            typeof(AIHub.MainWindow).GetField("_fullExitRequested", flags)!.SetValue(host, true);
            host.Close();
            Check(!closed, "Full exit waits for active video finalization");
            await videoTask!.WaitAsync(TimeSpan.FromSeconds(30));
            var exitDeadline = DateTime.UtcNow.AddSeconds(10);
            while (!closed && DateTime.UtcNow < exitDeadline) await Task.Delay(100);
            Check(closed && Directory.GetFiles(settings.ScreenCapture.Folder, "*.mp4").Length == 3, "Full exit saves video before closing the window");
        }
        finally
        {
            typeof(AIHub.MainWindow).GetField("_fullExitRequested", flags)!.SetValue(host, true);
            typeof(AIHub.MainWindow).GetField("_processShutdownComplete", flags)!.SetValue(host, true); host.Close();
        }
    }
#endif
    private static async Task DeliveryChecks(string run, BitmapSource original, BitmapSource scaled)
    {
        var folder = Path.Combine(run, "delivery"); var writes = 0; var questions = 0; var text = false; uint sequence = 1;
        var settings = new ScreenCaptureSettings { Folder = folder };
        settings.Outputs[CaptureSource.Area] = new() { Clipboard = true, File = false };
        var delivery = new ScreenshotDelivery(() => text, () => sequence, b => { writes++; Check(ReferenceEquals(b, scaled), "Enlarged image used for clipboard"); }, () => { questions++; return false; });
        var result = await delivery.DeliverAsync(settings, CaptureSource.Area, original, scaled, default);
        Check(writes == 1 && questions == 0 && result.Files.Count == 0, "Clipboard only without text produces no files");
        text = true; writes = 0;
        result = await delivery.DeliverAsync(settings, CaptureSource.Area, original, scaled, default);
        Check(result.TextKept && writes == 0 && result.Files.Count == 2 && result.Files.All(File.Exists), "Refused text replacement preserves both insurance files");
        questions = 0;
        delivery = new(() => text, () => sequence, _ => writes++, () =>
        {
            questions++; Check(Directory.GetFiles(folder).Length >= 4, "Insurance files exist before asking permission");
            if (questions == 1) sequence++; return true;
        });
        result = await delivery.DeliverAsync(settings, CaptureSource.Area, original, scaled, default);
        Check(writes == 1 && questions == 2 && result.ClipboardWritten, "Changed clipboard requires renewed confirmation");
        settings.Outputs[CaptureSource.Area] = new() { Clipboard = false, File = true }; writes = 0; questions = 0;
        result = await delivery.DeliverAsync(settings, CaptureSource.Area, original, scaled, default);
        Check(result.Files.Count == 2 && writes == 0 && questions == 0, "File only leaves clipboard untouched");
        settings.Outputs[CaptureSource.Area] = new() { Clipboard = true, File = true }; text = false;
        result = await delivery.DeliverAsync(settings, CaptureSource.Area, original, scaled, default);
        Check(result.Files.Count == 2 && writes == 1, "Combined output writes files and clipboard");
        delivery = new(() => false, () => sequence, _ => throw new System.Runtime.InteropServices.ExternalException("Clipboard busy"), () => true);
        try { await delivery.DeliverAsync(settings, CaptureSource.Area, original, scaled, default); throw new Exception("Busy clipboard should fail"); }
        catch (ScreenshotDeliveryException error) { Check(error.Files.Count == 2 && error.Files.All(File.Exists), "Clipboard failure preserves files and reports their paths"); }
        var enormous = BitmapSource.Create(10_000, 1, 96, 96, PixelFormats.Pbgra32, null, new byte[40_000], 40_000); enormous.Freeze();
        try { ScreenshotFiles.Enlarge(enormous); throw new Exception("Excessive enlargement should fail"); }
        catch (InvalidOperationException) { Check(true, "Extreme skinny crop enlargement is bounded before allocation"); }
    }
    private static async Task DialogChecks(Window owner, Func<string, string> text, string folder, string suffix)
    {
        using var recorder = new CaptureHotkeys(new WindowInteropHelper(owner).Handle);
        GetCursorPos(out var originalPointer);
        var dialog = new CaptureHotkeyWindow(owner, recorder, text); Exception? failure = null;
        dialog.Loaded += async (_, _) =>
        {
            try
            {
                await Task.Delay(160);
                Check(ReferenceEquals(dialog.Background, owner.Resources["WindowBackgroundBrush"]), "Dialog resolves owner-only background " + suffix);
                var held = Descendants<TextBlock>(dialog).Single(t => Id(t) == "Capture.Held");
                var selected = Descendants<TextBlock>(dialog).Single(t => Id(t) == "Capture.Selection");
                Check(ReferenceEquals(held.Foreground, owner.Resources["TextPrimaryBrush"]), "Held text resolves owner-only foreground " + suffix);
                Check(held.ActualHeight > 0 && selected.Text.Contains(text("Capture.Selection")), "Dialog instructions and labels rendered " + suffix);
                dialog.Activate(); dialog.Focus();
                // X1 goes to the window under the pointer; keep the synthetic click inside our dialog.
                GetWindowRect(new WindowInteropHelper(dialog).Handle, out var dialogRect);
                SetCursorPos((dialogRect.Left + dialogRect.Right) / 2, (dialogRect.Top + dialogRect.Bottom) / 2);
                keybd_event(0x11, 0, 0, 0); keybd_event(0x7C, 0, 0, 0); keybd_event(0x7D, 0, 0, 0); mouse_event(0x80, 0, 0, 1, 0);
                var inputWait = System.Diagnostics.Stopwatch.StartNew();
                while (!(held.Text.Contains("F13") && held.Text.Contains("Mouse X1") && dialog.Selection.Length == 4) && inputWait.Elapsed < TimeSpan.FromSeconds(2))
                    await Task.Delay(40);
                Check(held.Text.Contains("F13") && held.Text.Contains("Mouse X1") && dialog.Selection.Length == 4,
                    "Real dialog displays held mixed four-button chord " + suffix + " active=" + dialog.IsActive + " held=" + held.Text);
                mouse_event(0x100, 0, 0, 1, 0); keybd_event(0x7D, 0, 2, 0); keybd_event(0x7C, 0, 2, 0); keybd_event(0x11, 0, 2, 0);
                var releaseWait = System.Diagnostics.Stopwatch.StartNew();
                while (held.Text.Contains("F13") && releaseWait.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(40);
                Check(dialog.Selection.Length == 4 && selected.Text.Contains("F14") && !held.Text.Contains("F13"), "Selection remains visible after keys released " + suffix);
                Shot(dialog, folder, "dialog-" + suffix);
                var save = FindButtons(dialog).Single(b => (string?)b.Content == text("Capture.Save"));
                Check(save.IsEnabled, "Recorded combination can be saved " + suffix); save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            catch (Exception error) { failure = error; dialog.Close(); }
            finally
            {
                mouse_event(0x100, 0, 0, 1, 0);
                foreach (var key in new byte[] { 0x7D, 0x7C, 0x11 }) keybd_event(key, 0, 2, 0);
                SetCursorPos(originalPointer.X, originalPointer.Y);
            }
        };
        var accepted = dialog.ShowDialog();
        if (failure is not null) throw failure;
        Check(accepted == true, "Dialog confirms selected chord " + suffix);
        await Task.CompletedTask;
    }
    private static string Id(DependencyObject value) => System.Windows.Automation.AutomationProperties.GetAutomationId(value);
    private static string CaptureUiOptionName(ComboBox value) => value.SelectedItem?.ToString() ?? "";
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T value) yield return value;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void Shot(FrameworkElement element, string folder, string name)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(folder, name + ".png")); encoder.Save(stream);
    }
    private static IEnumerable<Button> FindButtons(DependencyObject root)
    {
        if (root is Button b) yield return b;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in FindButtons(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, nuint extra);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint hwnd, uint message, nint wp, nint lp);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint value);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [StructLayout(LayoutKind.Sequential)] private struct Pointer { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Pointer point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
}
