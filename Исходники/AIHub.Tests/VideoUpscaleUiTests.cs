using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Controls.VideoUpscale;
using AIHub.Services;
using AIHub.Services.VideoUpscale;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoUpscaleUiTests
{
    [TestMethod]
    public async Task ThreeRealWindowsHaveDifferentDetailAndTwoInitializedPlayers()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => {
            var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () => {
                try { await Run(); done.TrySetResult(); } catch (Exception error) { done.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }); Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); await done.Task;
    }
    private static async Task Run()
    {
        var folder = Environment.GetEnvironmentVariable("LOPATA_UPSCALE_UI_EVIDENCE") ?? Path.Combine(Path.GetTempPath(), "lopata-upscale-ui"); Directory.CreateDirectory(folder);
        using var storage = UpscaleStore.UseTestRoot(Path.Combine(folder, "projects"));
        using var audit = ComponentAudit.UseTestStore(Path.Combine(folder, "audit"), false);
        var confirm = ComponentLicenseGate.ConfirmAsync; ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
        var owner = new Window { Width = 100, Height = 100, ShowActivated = false };
        owner.Resources["WindowBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(14, 22, 38)); owner.Resources["TextPrimaryBrush"] = Brushes.WhiteSmoke; owner.Resources["TextSecondaryBrush"] = Brushes.LightSlateGray;
        owner.Resources["LineBrush"] = Brushes.SlateGray; owner.Resources["PanelBrush"] = new SolidColorBrush(Color.FromRgb(23, 32, 49));
        owner.Resources["SecondaryButtonBackgroundBrush"] = owner.Resources["WindowBackgroundBrush"]; owner.Resources["AccentBrush"] = Brushes.RoyalBlue; owner.Resources["UiBodyFontSize"] = 14d;
        var localizer = new LocalizationService(); localizer.Load("ru");
        try {
            owner.Show();
            await UpscaleFfmpeg.PrepareAsync(CancellationToken.None);
            var source = Path.Combine(folder, "ui-source.mp4");
            await UpscaleFfmpeg.RunAsync(false, ["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=24:duration=1", "-c:v", "libopenh264", "-b:v", "5M", source], CancellationToken.None);
            var media = await UpscaleMediaTools.Describe(source, CancellationToken.None);
            foreach (var mode in Enum.GetValues<UpscaleMode>()) {
                var p = new UpscaleProject { Name = "Тестовый ролик", Mode = mode, Steps = UpscaleCatalog.Preset(mode), Source = source, Media = media,
                    SourceLength = new FileInfo(source).Length, SourceSha256 = await UpscaleMediaTools.Hash(source, CancellationToken.None), PreviewLength = 1, PreviewPath = source };
                p.PreviewFingerprint = UpscaleCatalog.Fingerprint(p) + "|0|1";
                var window = new UpscaleWindow(owner, localizer.T, p) { ShowActivated = false };
                try {
                    window.Show(); await window.Ready.Task.WaitAsync(TimeSpan.FromSeconds(60)); window.UpdateLayout();
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert.AreEqual(2, Descendants(window).OfType<Microsoft.Web.WebView2.Wpf.WebView2>().Count());
                    var players = Descendants(window).OfType<Microsoft.Web.WebView2.Wpf.WebView2>().ToArray();
                    foreach (var player in players) {
                        var ready = false;
                        for (var n = 0; n < 100 && !ready; n++) {
                            ready = await player.CoreWebView2.ExecuteScriptAsync("[...document.querySelectorAll('video')].some(v=>v.readyState>=2)") == "true";
                            if (!ready) await Task.Delay(50);
                        }
                        Assert.IsTrue(ready, "Actual media loaded into both preview players");
                        Assert.AreEqual("true", await player.CoreWebView2.ExecuteScriptAsync("(()=>{const r=document.querySelector('video').getBoundingClientRect();return r.width>10&&r.height>10})()"), "Decoded video has visible dimensions");
                    }
                    Descendants(window).OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "VideoUpscale.NextFrame").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Task.Delay(250);
                    foreach (var player in players) {
                        var at = double.Parse(await player.CoreWebView2.ExecuteScriptAsync("document.querySelector('video').currentTime"), System.Globalization.CultureInfo.InvariantCulture);
                        Assert.IsTrue(Math.Abs(at - 1d / 24) < .025, "Paired frame stepping");
                    }
                    var buttons = Descendants(window).OfType<Button>().Select(AutomationProperties.GetAutomationId).ToArray();
                    Assert.AreEqual(mode == UpscaleMode.Manual, buttons.Contains("VideoUpscale.SavePreset"));
                    Assert.IsTrue(window.ActualWidth >= 1050 && window.ActualHeight >= 700);
                    if (mode == UpscaleMode.Manual) { p.Steps[0].Width = 1000; p.Steps[0].Height = 1000; }
                    var settingsCheck = window.Dispatcher.InvokeAsync(() => {
                        var dialog = window.OwnedWindows.OfType<Window>().Single(w => w.IsVisible);
                        try { CheckSettings(dialog, p, folder); }
                        finally { dialog.Close(); }
                    }, DispatcherPriority.Background);
                    Descendants(window).OfType<Button>().First(b => AutomationProperties.GetAutomationId(b).StartsWith("VideoUpscale.StepSettings.", StringComparison.Ordinal)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await settingsCheck.Task;
                    foreach (var size in new[] { (1500d, 950d, 14d), (1900d, 1040d, 22d), (1050d, 720d, 22d) }) {
                        owner.Resources["UiBodyFontSize"] = size.Item3; window.Width = size.Item1; window.Height = size.Item2;
                        window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        await Task.Delay(150);
                        await Capture(window, players, Path.Combine(folder, $"{mode}-{size.Item1}.png"));
                        AssertWorkspace(window);
                        typeof(UpscaleWindow).GetMethod("SetBusy", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, [true]);
                        window.UpdateLayout(); AssertWorkspace(window);
                        if (size.Item1 == 1900) await Capture(window, players, Path.Combine(folder, mode + "-busy.png"));
                        typeof(UpscaleWindow).GetMethod("SetBusy", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, [false]);
                    }
                    if (mode == UpscaleMode.Manual) CheckManualSteps(window);
                } finally { window.Close(); }
            }
        } finally { owner.Close(); ComponentLicenseGate.ConfirmAsync = confirm; }
    }
    private static void CheckSettings(Window dialog, UpscaleProject project, string folder)
    {
        T Find<T>(string id) where T : FrameworkElement => Descendants(dialog).OfType<T>().Single(e => AutomationProperties.GetAutomationId(e) == id);
        void Select(string id) => Find<CheckBox>(id).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Assert.AreEqual("VideoUpscale.Settings", AutomationProperties.GetAutomationId(dialog));
        Assert.IsFalse(Descendants(dialog).OfType<System.Windows.Controls.ComboBox>().Any(), "No hidden dropdown choices");
        Assert.AreEqual(7, Descendants(dialog).OfType<CheckBox>().Count(e => AutomationProperties.GetAutomationId(e).StartsWith("VideoUpscale.EngineChoice.")));
        var step = project.Steps.First(s => s.Engine == UpscaleEngine.Scale);
        if (project.Mode == UpscaleMode.Manual) {
            Assert.IsTrue(Find<CheckBox>("VideoUpscale.Resolution.1000x1000").IsChecked == true, "Legacy custom size is preserved on opening");
            Assert.AreEqual(1000, step.Width); Assert.AreEqual(1000, step.Height);
        }
        Select("VideoUpscale.Resolution.1080x1920"); Assert.AreEqual(1080, step.Width); Assert.AreEqual(1920, step.Height);
        var portrait = (DockPanel)Find<CheckBox>("VideoUpscale.Resolution.1080x1920").Parent;
        var shape = portrait.Children.OfType<Grid>().Single().Children.OfType<Border>().Single(); Assert.IsTrue(shape.Width < shape.Height, "Portrait frame example has portrait proportions");
        Select("VideoUpscale.Resolution.3840x2160"); Select("VideoUpscale.Resolution.3840x2160");
        Assert.AreEqual(1, Descendants(dialog).OfType<CheckBox>().Count(e => AutomationProperties.GetAutomationId(e).StartsWith("VideoUpscale.Resolution.") && e.IsChecked == true));
        Assert.IsTrue(Find<CheckBox>("VideoUpscale.Resolution.3840x2160").IsChecked == true, "Selected choice cannot be turned off");
        Find<Slider>("VideoUpscale.Mix.Slider").Value = .42; Assert.AreEqual(.42, step.Mix, .000001);
        Select("VideoUpscale.Algorithm.bicubic"); Assert.AreEqual("bicubic", step.Algorithm);
        Find<Slider>("VideoUpscale.Param.sharpen.Slider").Value = 1.37; Assert.AreEqual("1.37", step.Parameters["sharpen"]);
        foreach (var engine in Enum.GetValues<UpscaleEngine>()) {
            Select("VideoUpscale.EngineChoice." + engine); Assert.AreEqual(engine, step.Engine);
            Assert.AreEqual(1, Descendants(dialog).OfType<CheckBox>().Count(e => AutomationProperties.GetAutomationId(e).StartsWith("VideoUpscale.EngineChoice.") && e.IsChecked == true));
            if (engine == UpscaleEngine.Flash) { Find<Slider>("VideoUpscale.Param.decode_every.Slider").Value = 6; Assert.AreEqual(0, int.Parse(step.Parameters["decode_every"]) % 4); }
        }
        Select("VideoUpscale.EngineChoice.Scale"); dialog.UpdateLayout(); CaptureWpf(dialog, Path.Combine(folder, project.Mode + "-settings.png"));
    }
    private static void CheckManualSteps(UpscaleWindow window)
    {
        var before = window.Project.Steps.Count;
        var check = window.Dispatcher.InvokeAsync(() => {
            var dialog = window.OwnedWindows.OfType<Window>().Single(w => w.IsVisible);
            try {
                Assert.AreEqual(7, Descendants(dialog).OfType<CheckBox>().Count());
                Descendants(dialog).OfType<CheckBox>().Single(e => AutomationProperties.GetAutomationId(e) == "VideoUpscale.EngineChoice.Flash").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Descendants(dialog).OfType<Button>().Single(e => AutomationProperties.GetAutomationId(e) == "VideoUpscale.AddSelectedStep").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            } finally { if (dialog.IsVisible) dialog.Close(); }
        }, DispatcherPriority.Background);
        Descendants(window).OfType<Button>().Single(e => AutomationProperties.GetAutomationId(e) == "VideoUpscale.AddStep").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check.Task.GetAwaiter().GetResult(); Assert.AreEqual(before + 1, window.Project.Steps.Count); Assert.AreEqual(UpscaleEngine.Flash, window.Project.Steps[^1].Engine);
        foreach (var step in window.Project.Steps.ToArray()) Descendants(window).OfType<Button>().Single(e => AutomationProperties.GetAutomationId(e) == "VideoUpscale.RemoveStep." + step.Id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.AreEqual(0, window.Project.Steps.Count);
        Assert.AreEqual(0, UpscaleStore.Load<UpscaleProject>(UpscaleStore.ProjectPath(window.Project.Id)).Steps.Count, "Deleting all steps persists an editable draft");
    }
    private static void CaptureWpf(Window window, string path)
    {
        var root = (FrameworkElement)window.Content; var dpi = VisualTreeHelper.GetDpi(root); var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen()) dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        var capture = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32); capture.Render(drawing);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(capture)); using var file = File.Create(path); png.Save(file);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    { yield return parent; foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>()) foreach (var item in Descendants(child)) yield return item; }
    private static void AssertWorkspace(UpscaleWindow window)
    {
        var root = (FrameworkElement)window.Content;
        FrameworkElement Item(string name) => Descendants(window).OfType<FrameworkElement>().Single(e => AutomationProperties.GetAutomationId(e) == "VideoUpscale." + name);
        Rect Bounds(FrameworkElement element) => new(element.TranslatePoint(new System.Windows.Point(), root), element.RenderSize);
        var comparison = Bounds(Item("Comparison")); var transport = Bounds(Item("Transport")); var timeline = Bounds(Item("Timeline"));
        Assert.IsTrue(comparison.Bottom <= transport.Top + 1, "Native video must not overlap transport");
        Assert.IsTrue(transport.Bottom <= timeline.Top + 1, "Transport must remain above timeline");
        Assert.IsTrue(comparison.Height > root.ActualHeight * (window.Width > 1200 ? .4 : .22), $"Preview retains primary screen area: window={window.Width}, root={root.ActualHeight}, preview={comparison.Height}");
        foreach (var id in new[] { "Import", "Play", "NextFrame", "Preview", "Cancel", "Journal" }) {
            var bounds = Bounds(Item(id)); Assert.IsTrue(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= root.ActualWidth + 1 && bounds.Bottom <= root.ActualHeight + 1, id + " is visible inside workspace");
        }
        foreach (var player in Descendants(window).OfType<Microsoft.Web.WebView2.Wpf.WebView2>()) {
            var bounds = Bounds(player); Assert.IsTrue(bounds.Top >= comparison.Top && bounds.Bottom <= comparison.Bottom + 1 && bounds.Width > 100 && bounds.Height > 60, "Both player HWNDs stay inside comparison area");
        }
    }
    private static async Task Capture(UpscaleWindow window, Microsoft.Web.WebView2.Wpf.WebView2[] players, string path)
    {
        // Capture the actual native surfaces too; WPF RenderTargetBitmap alone omits WebView2.
        var root = (FrameworkElement)window.Content; var dpi = VisualTreeHelper.GetDpi(root);
        var width = (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX); var height = (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) {
            dc.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.Fill }, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            foreach (var player in players) {
                using var stream = new MemoryStream(); await player.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, stream); stream.Position = 0;
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
                dc.DrawImage(bitmap, new Rect(player.TranslatePoint(new System.Windows.Point(), root), player.RenderSize));
            }
        }
        var capture = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32); capture.Render(visual);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(capture)); using var file = File.Create(path); png.Save(file);
    }
}
