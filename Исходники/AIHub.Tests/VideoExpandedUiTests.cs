using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Controls.VideoEditor;
using AIHub.Services;
using AIHub.Services.VideoEditor;
using Microsoft.Web.WebView2.Core;
using Brushes = System.Windows.Media.Brushes;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoExpandedUiTests
{
    [TestMethod]
    public async Task HoverButtonExpandsSamePlayingBrowserAndEscapeRestoresWorkspace()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lopata-video-expand-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        await OnUi(async () => {
            using var policy = ComponentAudit.UseTestStore(Path.Combine(directory, "audit"), false);
            var oldGate = ComponentLicenseGate.ConfirmAsync; ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
            var owner = new Window { Width = 100, Height = 100, ShowActivated = false };
            owner.Resources["WindowBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(14, 22, 38)); owner.Resources["TextPrimaryBrush"] = Brushes.WhiteSmoke; owner.Resources["LineBrush"] = Brushes.SlateGray;
            owner.Resources["PanelBrush"] = new SolidColorBrush(Color.FromRgb(23, 32, 49)); owner.Resources["SecondaryButtonBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(16, 24, 40)); owner.Resources["AccentBrush"] = Brushes.RoyalBlue; owner.Resources["UiBodyFontSize"] = 14d;
            var l = new LocalizationService(); l.Load("ru"); VideoEditorWindow? editor = null;
            try {
                var source = Path.Combine(directory, "blue.mp4");
                await VideoRuntime.PrepareAsync(default); await VideoRuntime.RunAsync(false, ["-v", "error", "-f", "lavfi", "-i", "color=c=blue:s=320x180:d=8:r=12", "-c:v", "libopenh264", "-an", source], default);
                var tone = Path.Combine(directory, "tone.wav"); using (var writer = new BinaryWriter(File.Create(tone))) { const int samples = 48000 * 8; writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples * 2); writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(48000); writer.Write(96000); writer.Write((short)2); writer.Write((short)16); writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(samples * 2); for (var i = 0; i < samples; i++) writer.Write((short)(Math.Sin(i * 2 * Math.PI * 440 / 48000) * 6000)); }
                var p = new VideoProject { Name = "Развёрнутый просмотр" }; VideoImport.Insert(p, await VideoImport.ReadAsync(source, directory, default), 0); VideoImport.Insert(p, await VideoImport.ReadAsync(tone, directory, default), 0);
                var portable = Path.Combine(directory, "Native portable.lopataVideo"); VideoProjectArchive.Save(p, portable);
                File.Move(source, source + ".unavailable"); File.Move(tone, tone + ".unavailable"); p = VideoProjectStore.Load(portable);
                owner.Show(); editor = new(owner, l.T, "ru", new VideoWork(), p) { ConfirmSaveOnClose = () => VideoCloseChoice.Discard, ShowActivated = false, Width = 1440, Height = 950 }; editor.Show(); await editor.Ready.Task.WaitAsync(TimeSpan.FromSeconds(60));
                var preview = Descendants(editor).OfType<VideoPreview>().Single();
                await preview.CoreWebView2.ExecuteScriptAsync("window.originalMedia=[...document.querySelectorAll('video,audio')]; window.lopata.seek(1); window.lopata.play(true)"); await Task.Delay(250);
                var originalSize = preview.RenderSize; var before = await State(preview);
                await preview.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", "{\"type\":\"mouseMoved\",\"x\":100,\"y\":100}"); await Task.Delay(220);
                var opacity = await preview.CoreWebView2.ExecuteScriptAsync("getComputedStyle(document.getElementById('expandPreview')).opacity"); Assert.AreEqual("\"1\"", opacity);
                await ClickExpand(preview); await Task.Delay(220);
                Assert.IsTrue(editor.PreviewExpanded); Assert.IsTrue(preview.ActualWidth > originalSize.Width * 1.4); Assert.IsTrue(preview.ActualHeight > originalSize.Height * 1.5);
                Assert.AreEqual("true", await preview.CoreWebView2.ExecuteScriptAsync("originalMedia.every(e=>e.isConnected && e===document.querySelector(e.tagName.toLowerCase()))"));
                var expanded = await State(preview); Assert.IsTrue(expanded.GetProperty("playing").GetBoolean()); Assert.IsTrue(expanded.GetProperty("time").GetDouble() >= before.GetProperty("time").GetDouble()); Assert.IsTrue(expanded.GetProperty("audioRms").GetDouble() > .01);
                var evidence = Environment.GetEnvironmentVariable("LOPATA_VIDEO_PORTABLE_EVIDENCE"); if (!string.IsNullOrEmpty(evidence)) { Directory.CreateDirectory(evidence); File.Copy(portable, Path.Combine(evidence, "Native portable.lopataVideo"), true); await using var png = File.Create(Path.Combine(evidence, "expanded-player.png")); await preview.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, png); }
                await preview.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", "{\"type\":\"keyDown\",\"key\":\"Escape\",\"code\":\"Escape\",\"windowsVirtualKeyCode\":27}");
                await preview.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", "{\"type\":\"keyUp\",\"key\":\"Escape\",\"code\":\"Escape\",\"windowsVirtualKeyCode\":27}"); await Task.Delay(180);
                Assert.IsFalse(editor.PreviewExpanded); Assert.AreEqual(originalSize.Width, preview.ActualWidth, 2); Assert.AreEqual(originalSize.Height, preview.ActualHeight, 2);
                await ClickExpand(preview); await Task.Delay(100); Assert.IsTrue(editor.PreviewExpanded); await ClickExpand(preview); await Task.Delay(100); Assert.IsFalse(editor.PreviewExpanded);
                await preview.PlayAsync(false);
                if (!string.IsNullOrEmpty(evidence)) {
                    await File.WriteAllTextAsync(Path.Combine(evidence, "expanded.json"), JsonSerializer.Serialize(new { SameBrowser = true, EscapeRestores = true, ButtonRestores = true, ExpandedAudioRms = expanded.GetProperty("audioRms").GetDouble(), BeforeTime = before.GetProperty("time").GetDouble(), ExpandedTime = expanded.GetProperty("time").GetDouble() }));
                    editor.UpdateLayout(); var image = new RenderTargetBitmap((int)editor.ActualWidth, (int)editor.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(editor); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var output = File.Create(Path.Combine(evidence, "restored-workspace.png")); encoder.Save(output);
                }
            }
            finally { editor?.Close(); owner.Close(); ComponentLicenseGate.ConfirmAsync = oldGate; }
        });
        Directory.Delete(directory, true);
    }
    private static async Task<JsonElement> State(VideoPreview p) { using var doc = JsonDocument.Parse(await p.CoreWebView2.ExecuteScriptAsync("window.lopata.diagnostics()")); return doc.RootElement.Clone(); }
    private static async Task ClickExpand(VideoPreview p)
    {
        using var doc = JsonDocument.Parse(await p.CoreWebView2.ExecuteScriptAsync("(()=>{const r=document.getElementById('expandPreview').getBoundingClientRect();return{x:r.x+r.width/2,y:r.y+r.height/2}})()"));
        var x = doc.RootElement.GetProperty("x").GetDouble(); var y = doc.RootElement.GetProperty("y").GetDouble();
        foreach (var type in new[] { "mouseMoved", "mousePressed", "mouseReleased" }) await p.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type, x, y, button = type == "mouseMoved" ? "none" : "left", clickCount = 1 }));
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    { yield return root; foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) foreach (var item in Descendants(child)) yield return item; }
    private static Task OnUi(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher)); dispatcher.BeginInvoke(async () => { try { await action(); done.TrySetResult(); } catch (Exception error) { done.TrySetException(error); } finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } }); Dispatcher.Run(); }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task;
    }
}
