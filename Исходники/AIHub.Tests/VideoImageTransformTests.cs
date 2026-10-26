using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Controls.VideoEditor;
using AIHub.Services;
using AIHub.Services.VideoEditor;
using Microsoft.Web.WebView2.Core;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using CheckBox = System.Windows.Controls.CheckBox;
using Brushes = System.Windows.Media.Brushes;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoImageTransformTests
{
    private static string Evidence => Environment.GetEnvironmentVariable("LOPATA_IMAGE_TRANSFORM_EVIDENCE") ?? Path.Combine(Path.GetTempPath(), "lopata-image-transform");
    private static string Fixture(string root, string kind)
    {
        Directory.CreateDirectory(root); var path = Path.Combine(root, "two-colours." + kind);
        if (kind == "svg") { File.WriteAllText(path, "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 80 40'><path fill='red' d='M0 0H40V40H0Z'/><path fill='lime' d='M40 0H80V40H40Z'/></svg>"); return path; }
        var pixels = new byte[80 * 40 * 4];
        for (var y = 0; y < 40; y++) for (var x = 0; x < 80; x++) { var i = (y * 80 + x) * 4; pixels[i + (x < 40 ? 2 : 1)] = 255; pixels[i + 3] = (byte)(kind == "png" && (x < 2 || x >= 78 || y < 2 || y >= 38) ? 0 : 255); }
        var bitmap = BitmapSource.Create(80, 40, 96, 96, PixelFormats.Bgra32, null, pixels, 320);
        BitmapEncoder encoder = kind == "jpg" ? new JpegBitmapEncoder { QualityLevel = 95 } : new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file); return path;
    }
    [TestMethod]
    public async Task PictureHandlesSelectionUndoPreciseInputAndInsertionWorkInRealBrowser()
    {
        var root = Path.Combine(Evidence, "ui"); Directory.CreateDirectory(root);
        await OnUi(async () => {
            using var policy = ComponentAudit.UseTestStore(Path.Combine(root, "audit"), false);
            var gate = ComponentLicenseGate.ConfirmAsync; ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
            var owner = new Window { Width = 100, Height = 100, ShowActivated = false };
            foreach (var key in new[] { "WindowBackgroundBrush", "TextPrimaryBrush", "TextSecondaryBrush", "LineBrush", "PanelBrush", "SecondaryButtonBackgroundBrush", "AccentBrush", "StepBadgeBrush" }) owner.Resources[key] = key == "TextPrimaryBrush" ? Brushes.WhiteSmoke : Brushes.SlateGray;
            owner.Resources["UiBodyFontSize"] = 14d; var l = new LocalizationService(); l.Load("ru"); VideoEditorWindow? editor = null;
            try {
                await VideoRuntime.PrepareAsync(default);
                var p = new VideoProject { Width = 320, Height = 180, Name = "Размер и поворот картинки", LinkedSelectionEnabled = false };
                var background = await VideoImport.ReadAsync(Fixture(root, "jpg"), root, default); VideoImport.Insert(p, background, 0);
                var image = await VideoImport.ReadAsync(Fixture(root, "png"), root, default); VideoImport.Insert(p, image, 0);
                var clip = p.Tracks.Last().Clips.Single(); clip.Scale = .5;
                owner.Show(); editor = new(owner, l.T, "ru", new VideoWork(), p) { ConfirmSaveOnClose = () => VideoCloseChoice.Discard, ShowActivated = false, Width = 1500, Height = 950 }; editor.Show(); await editor.Ready.Task.WaitAsync(TimeSpan.FromSeconds(60));
                var preview = Descendants(editor).OfType<VideoPreview>().Single(); var timeline = Descendants(editor).OfType<VideoTimeline>().Single();
                timeline.SelectItems(p.Tracks.SelectMany(t => t.Clips).Select(c => new VideoItemId(c.Id))); Assert.AreEqual(2, timeline.Selection.Count);
                var framing = Descendants(editor).OfType<CheckBox>().Single(c => AutomationProperties.GetAutomationId(c) == "VideoEditor.FrameMode"); framing.IsChecked = true; framing.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(200);
                async Task<Point> Centre(string selector) { using var json = JsonDocument.Parse(await preview.CoreWebView2.ExecuteScriptAsync($"(()=>{{const r=document.querySelector({JsonSerializer.Serialize(selector)}).getBoundingClientRect();return {{x:r.left+r.width/2,y:r.top+r.height/2}}}})()")); return new(json.RootElement.GetProperty("x").GetDouble(), json.RootElement.GetProperty("y").GetDouble()); }
                async Task Mouse(string type, Point at) => await preview.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type, x = at.X, y = at.Y, button = "left", clickCount = 1 }));
                var center = await Centre("#stage"); await Mouse("mousePressed", center); await Mouse("mouseReleased", center); await Task.Delay(200);
                Assert.AreEqual(1, timeline.Selection.Count); Assert.AreEqual(clip.Id, timeline.SelectedClip, "Preview click must replace the previous group selection with the top image");
                var corner = await Centre("[data-frame-handle=se]"); var larger = new Point(center.X + (corner.X - center.X) * 1.5, center.Y + (corner.Y - center.Y) * 1.5);
                await Mouse("mousePressed", corner); await Mouse("mouseMoved", larger); await Task.Delay(1250); await Mouse("mouseReleased", larger); await Task.Delay(160);
                Assert.AreEqual(.75, clip.Scale, .006, "A resize drag must survive a pending recovery timer");
                var rotate = await Centre("[data-frame-handle=rotate]"); var right = new Point(center.X - (rotate.Y - center.Y), center.Y + rotate.X - center.X);
                await Mouse("mousePressed", rotate); await Mouse("mouseMoved", right); await Mouse("mouseReleased", right); await Task.Delay(160);
                Assert.AreEqual(90, clip.Rotation, .5);
                void Click(string id) => Descendants(editor).OfType<Button>().First(b => AutomationProperties.GetAutomationId(b) == "VideoEditor." + id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Click("Undo"); await Task.Delay(200); Assert.AreEqual(0, timeline.Project.Tracks.Last().Clips.Single().Rotation); Assert.AreEqual(.75, timeline.Project.Tracks.Last().Clips.Single().Scale, .006, "One undo must undo exactly one gesture");
                Click("Redo"); await Task.Delay(200); Assert.AreEqual(90, timeline.Project.Tracks.Last().Clips.Single().Rotation, .5);
                corner = await Centre("[data-frame-handle=se]"); await Mouse("mousePressed", corner); await Mouse("mouseMoved", new(corner.X + 20, corner.Y + 30));
                await preview.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", "{\"type\":\"keyDown\",\"key\":\"Escape\",\"code\":\"Escape\",\"windowsVirtualKeyCode\":27}"); await Mouse("mouseReleased", corner); await Task.Delay(150);
                Assert.AreEqual(.75, timeline.Project.Tracks.Last().Clips.Single().Scale, .006);
                var dialogDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _ = editor.Dispatcher.BeginInvoke(() => {
                    try {
                        var dialog = editor.OwnedWindows.Cast<Window>().Single();
                        TextBox Input(string key) => Descendants(dialog).OfType<TextBox>().Single(t => AutomationProperties.GetAutomationId(t) == "VideoEditor.Transform." + key);
                        var apply = Descendants(dialog).OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "VideoEditor.Apply");
                        Input("SizePercent").Text = "0"; apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.IsTrue(dialog.IsVisible, "Invalid size must keep the dialog open");
                        Input("SizePercent").Text = "62,5"; Input("Rotation").Text = "30"; Input("X").Text = "12"; Input("Y").Text = "8"; apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); dialogDone.SetResult();
                    } catch (Exception e) { foreach (Window d in editor.OwnedWindows) d.Close(); dialogDone.SetException(e); }
                });
                Click("Transform"); await dialogDone.Task; await Task.Delay(1400);
                var transformed = timeline.Project.Tracks.Last().Clips.Single(); Assert.AreEqual(.625, transformed.Scale); Assert.AreEqual(30, transformed.Rotation); Assert.AreEqual(12, transformed.X);
                var saved = VideoProjectStore.Load(VideoProjectStore.Recovery(editor.ProjectId)); Assert.AreEqual(.625, saved.Tracks.Last().Clips.Single().Scale); Assert.AreEqual(30, saved.Tracks.Last().Clips.Single().Rotation);
                // All four handles must preserve the centre and proportions after translation/rotation.
                center = await Centre("#framingBox"); var expectedScale = .625;
                foreach (var key in new[] { "nw", "ne", "sw", "se" }) {
                    corner = await Centre("[data-frame-handle=" + key + "]"); var smaller = new Point(center.X + (corner.X - center.X) * .9, center.Y + (corner.Y - center.Y) * .9);
                    await Mouse("mousePressed", corner); await Mouse("mouseMoved", smaller); await Mouse("mouseReleased", smaller); await Task.Delay(100); expectedScale *= .9;
                    Assert.AreEqual(expectedScale, transformed.Scale, .007); Assert.AreEqual(12, transformed.X); Assert.AreEqual(8, transformed.Y); Assert.AreEqual(30, transformed.Rotation);
                }
                await using (var shot = File.Create(Path.Combine(root, "handles.png"))) await preview.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, shot);
                // A new material replaces stale selection and enables framing even on the Materials tab.
                timeline.SelectItems(timeline.Project.Tracks.SelectMany(t => t.Clips).Select(c => new VideoItemId(c.Id)));
                var materials = Descendants(editor).OfType<System.Windows.Controls.ListBox>().Single(b => b.DisplayMemberPath == "Name"); materials.SelectedItem = materials.Items.Cast<VideoAsset>().Single(a => a.Id == image.Id);
                Click("InsertMaterial"); await Task.Delay(300); Assert.AreEqual(1, timeline.Selection.Count); Assert.AreNotEqual(transformed.Id, timeline.SelectedClip); Assert.IsTrue(framing.IsChecked == true);
                var track = timeline.Project.Tracks.Single(t => t.Clips.Any(c => c.Id == timeline.SelectedClip)); track.Locked = true;
                framing.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(150); Assert.AreEqual("\"none\"", await preview.CoreWebView2.ExecuteScriptAsync("document.getElementById('framingBox').style.display"));
                await File.WriteAllTextAsync(Path.Combine(root, "verification.json"), JsonSerializer.Serialize(new { GroupSelectionReplaced = true, ProportionalResize = true, Rotation = 90, UndoRedo = true, EscapeCancels = true, PreciseInput = true, Recovery = true, InsertionSelectsNewImage = true, LockedTrackProtected = true }));
            } finally { editor?.Close(); owner.Close(); ComponentLicenseGate.ConfirmAsync = gate; }
        });
    }
    [TestMethod]
    public async Task PngJpegSvgRotationScaleAndTransparencySurvivePortableProjectAndExport()
    {
        var root = Path.Combine(Evidence, "export", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        using var policy = ComponentAudit.UseTestStore(Path.Combine(root, "audit"), false); var gate = ComponentLicenseGate.ConfirmAsync; ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
        try {
            await VideoRuntime.PrepareAsync(default);
            var results = new List<object>();
            foreach (var kind in new[] { "png", "jpg", "svg" }) {
                var path = Fixture(root, kind); var hash = SHA256.HashData(await File.ReadAllBytesAsync(path));
                var p = new VideoProject { Width = 320, Height = 180, Fps = 10, Name = "Проверка " + kind };
                var asset = await VideoImport.ReadAsync(path, root, default); VideoImport.Insert(p, asset, 0); var c = p.Tracks[0].Clips[0]; c.Duration = .6; c.Scale = .35; c.Rotation = 45; c.X = 30; c.Y = 12;
                var copy = JsonSerializer.Deserialize<VideoClip>(JsonSerializer.Serialize(c))!; copy.Id = Guid.NewGuid().ToString("N"); copy.Start = .6; copy.Rotation = 90; p.Tracks[0].Clips.Add(copy);
                var archive = Path.Combine(root, kind + ".lopataVideo"); VideoProjectArchive.Save(p, archive); p = VideoProjectStore.Load(archive);
                Assert.AreEqual(.35, p.Tracks[0].Clips[0].Scale); Assert.AreEqual(45, p.Tracks[0].Clips[0].Rotation); Assert.AreEqual(90, p.Tracks[0].Clips[1].Rotation);
                var output = Path.Combine(root, kind + ".mp4"); if (File.Exists(output)) File.Delete(output);
                await VideoExport.RunAsync(p, output, new(320, 180, 10, 0, 1.2, false, false), Guid.NewGuid().ToString("N"), null, default);
                foreach (var (at, angle) in new[] { (.2, 45d), (.8, 90d) }) {
                    var raw = Path.Combine(root, kind + angle + ".rgba"); if (File.Exists(raw)) File.Delete(raw);
                    await VideoRuntime.RunAsync(false, ["-v", "error", "-ss", VideoExport.N(at), "-i", output, "-frames:v", "1", "-pix_fmt", "rgba", "-f", "rawvideo", raw], default);
                    var bytes = await File.ReadAllBytesAsync(raw); var xs = new List<int>(); var ys = new List<int>();
                    for (var y = 0; y < 180; y++) for (var x = 0; x < 320; x++) { var i = (y * 320 + x) * 4; if (bytes[i] > 130 && bytes[i + 1] < 100 || bytes[i + 1] > 130 && bytes[i] < 100) { xs.Add(x); ys.Add(y); } }
                    Assert.IsTrue(xs.Count > 1000); Assert.AreEqual(190, (xs.Min() + xs.Max()) / 2d, 3); Assert.AreEqual(102, (ys.Min() + ys.Max()) / 2d, 3);
                    var g = VideoFraming.Geometry(p, asset, c); var radians = angle * Math.PI / 180; var border = kind == "png" ? .9 : 1;
                    Assert.AreEqual((g.Width * Math.Abs(Math.Cos(radians)) + g.Height * Math.Abs(Math.Sin(radians))) * border, xs.Max() - xs.Min() + 1, 9);
                    Assert.AreEqual((g.Width * Math.Abs(Math.Sin(radians)) + g.Height * Math.Abs(Math.Cos(radians))) * border, ys.Max() - ys.Min() + 1, 9);
                    Assert.IsTrue(bytes[(10 * 320 + 10) * 4] < 30, "Outside the transformed image must remain black");
                    results.Add(new { kind, angle, width = xs.Max() - xs.Min() + 1, height = ys.Max() - ys.Min() + 1 });
                }
                CollectionAssert.AreEqual(hash, SHA256.HashData(await File.ReadAllBytesAsync(path)));
                using var probe = await VideoRuntime.ProbeAsync(output, default); Assert.IsTrue(probe.RootElement.GetProperty("format").GetProperty("tags").ToString().Contains("LOPATA", StringComparison.Ordinal));
            }
            await File.WriteAllTextAsync(Path.Combine(root, "verification.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        } finally { ComponentLicenseGate.ConfirmAsync = gate; }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root) { yield return root; foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) foreach (var item in Descendants(child)) yield return item; }
    private static Task OnUi(Func<Task> action) {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher)); dispatcher.BeginInvoke(async () => { try { await action(); done.TrySetResult(); } catch (Exception e) { done.TrySetException(e); } finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } }); Dispatcher.Run(); }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task;
    }
}
