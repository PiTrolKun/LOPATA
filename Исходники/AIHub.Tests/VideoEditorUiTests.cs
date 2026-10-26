using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
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
public sealed class VideoEditorUiTests
{
    [TestMethod]
    public async Task WorkspaceRendersRealSvgAndPlaysProxyWithSharedPlayhead()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lopata-video-ui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        await OnUi(async () => {
            using var policy = ComponentAudit.UseTestStore(Path.Combine(directory, "audit"), false);
            var gate = ComponentLicenseGate.ConfirmAsync; ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
            var owner = new Window { Width = 100, Height = 100, ShowActivated = false, WindowStyle = WindowStyle.ToolWindow };
            owner.Resources["WindowBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(14, 22, 38)); owner.Resources["TextPrimaryBrush"] = Brushes.WhiteSmoke; owner.Resources["LineBrush"] = Brushes.SlateGray;
            owner.Resources["PanelBrush"] = new SolidColorBrush(Color.FromRgb(23, 32, 49)); owner.Resources["SecondaryButtonBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(16, 24, 40)); owner.Resources["AccentBrush"] = Brushes.RoyalBlue; owner.Resources["UiBodyFontSize"] = 14d;
            var localization = new LocalizationService(); localization.Load("ru"); VideoEditorWindow? editor = null;
            try
            {
                var source = Path.Combine(directory, "source.mp4");
                await VideoRuntime.RunAsync(false, ["-v", "error", "-n", "-f", "lavfi", "-i", "color=c=blue:s=320x180:r=12:d=3", "-c:v", "libopenh264", "-b:v", "500k", source], CancellationToken.None);
                var p = new VideoProject { Width = 320, Height = 180, Fps = 12, Name = "Проверка редактора" };
                var asset = await VideoImport.ReadAsync(source, directory, CancellationToken.None); VideoImport.Insert(p, asset, 0);
                var tone = Path.Combine(directory, "tone.wav");
                using (var writer = new BinaryWriter(File.Create(tone))) { var samples = 48000 * 3; writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples * 2); writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(48000); writer.Write(96000); writer.Write((short)2); writer.Write((short)16); writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(samples * 2); for (var sample = 0; sample < samples; sample++) writer.Write((short)(Math.Sin(sample * 2 * Math.PI * 440 / 48000) * 6000)); }
                var audio = await VideoImport.ReadAsync(tone, directory, CancellationToken.None); VideoImport.Insert(p, audio, 0);
                var svg = Path.Combine(directory, "overlay.svg"); await File.WriteAllTextAsync(svg, "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 320 180'><rect x='200' y='20' width='60' height='60' fill='red'/></svg>");
                var vector = await VideoImport.ReadAsync(svg, directory, CancellationToken.None); p.Assets.Add(vector); p.Tracks.Add(new() { Kind = "video", Name = "SVG", Clips = [new() { AssetId = vector.Id, Duration = 3 }] });
                p.Narration.Add(new() { Start = 0, End = 2, Text = "Проверяем субтитры и предпросмотр." });
                p.Narration.Add(new() { Start = 2, End = 2.8, Text = "Следующий фрагмент." });
                owner.Show(); editor = new(owner, localization.T, "ru", new VideoWork(), p) { ConfirmSaveOnClose = () => VideoCloseChoice.Discard, ShowActivated = false, Width = 1440, Height = 900 }; editor.Show();
                await editor.Ready.Task.WaitAsync(TimeSpan.FromSeconds(60));
                var preview = Descendants(editor).OfType<VideoPreview>().Single(); Assert.IsTrue(preview.Ready, "The real WebView2 preview must initialize");
                var selectionTimeline = Descendants(editor).OfType<VideoTimeline>().Single(); var segmentList = Descendants(editor).OfType<VideoSegmentList>().Single();
                foreach (var control in Descendants(editor).OfType<FrameworkElement>().Where(c => System.Windows.Automation.AutomationProperties.GetAutomationId(c).StartsWith("VideoEditor.", StringComparison.Ordinal) && c is System.Windows.Controls.Button or System.Windows.Controls.CheckBox or System.Windows.Controls.ComboBox or System.Windows.Controls.TextBox or System.Windows.Controls.Slider)) {
                    Assert.IsTrue(control.ToolTip is string tip && tip.Length is > 5 and <= 180 && !tip.StartsWith("VideoEditor.", StringComparison.Ordinal), "A short translated action hint is required: " + System.Windows.Automation.AutomationProperties.GetAutomationId(control));
                    Assert.IsFalse(System.Windows.Automation.AutomationProperties.GetName(control).StartsWith("VideoEditor.", StringComparison.Ordinal));
                }
                var compactSplit = Descendants(editor).OfType<System.Windows.Controls.Button>().Single(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "VideoEditor.Split"); Assert.IsInstanceOfType<Viewbox>(compactSplit.Content); Assert.IsTrue(compactSplit.Width <= 40);
                var seriesToggle = Descendants(editor).OfType<System.Windows.Controls.CheckBox>().Single(c => System.Windows.Automation.AutomationProperties.GetAutomationId(c) == "VideoEditor.SeriesSelection");
                seriesToggle.IsChecked = true; seriesToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); await Task.Delay(100);
                Assert.IsTrue(p.SelectBySeries); selectionTimeline.SelectItems([new(p.Tracks[0].Clips[0].Id)]); Assert.AreEqual(3, selectionTimeline.Selection.Count);
                seriesToggle.ContextMenu!.IsOpen = true; await Task.Delay(60);
                var includeText = seriesToggle.ContextMenu.Items.OfType<System.Windows.Controls.MenuItem>().Single(i => System.Windows.Automation.AutomationProperties.GetAutomationId(i) == "VideoEditor.SeriesLayer.Subtitles");
                includeText.IsChecked = false; includeText.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent)); await Task.Delay(100);
                Assert.IsFalse(p.SeriesLayers.HasFlag(VideoSeriesLayers.Subtitles)); Assert.AreEqual(1, selectionTimeline.Selection.Count); Assert.AreEqual(0, segmentList.SelectedItems.Count);
                includeText.IsChecked = true; includeText.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent)); seriesToggle.ContextMenu.IsOpen = false; await Task.Delay(60);
                var linkedToggle = Descendants(editor).OfType<System.Windows.Controls.CheckBox>().Single(c => System.Windows.Automation.AutomationProperties.GetAutomationId(c) == "VideoEditor.LinkedSelection");
                linkedToggle.IsChecked = true; linkedToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); await Task.Delay(100); Assert.IsFalse(p.SelectBySeries);
                selectionTimeline.SelectItems([new(p.Tracks[0].Clips[0].Id)]); Assert.AreEqual(2, segmentList.SelectedItems.Count, "Video selection must select its text fragments");
                var firstSubtitle = selectionTimeline.Blocks().First(b => b.Item.Subtitle);
                selectionTimeline.SelectItems([firstSubtitle.Item]); Assert.AreEqual(p.Narration[0], segmentList.SelectedItem);
                Assert.IsTrue(selectionTimeline.Selection.Contains(new(p.Tracks[0].Clips[0].Id)), "Text selection must select the matching video");
                segmentList.SelectAll(); Assert.AreEqual(3, selectionTimeline.Selection.Count); Assert.AreEqual(2, selectionTimeline.Selection.Items.Count(i => i.Subtitle));
                Assert.IsFalse(selectionTimeline.Selection.Contains(new(p.Tracks[1].Clips[0].Id)), "An independent tone must not become linked just because its time overlaps");
                var viewport = Descendants(editor).OfType<ScrollViewer>().Single(v => v.Content == selectionTimeline);
                Assert.IsTrue(selectionTimeline.ActualWidth >= viewport.ViewportWidth - 1); Assert.IsTrue(selectionTimeline.ActualHeight >= viewport.ViewportHeight - 1);
                selectionTimeline.SelectItems([]); Assert.AreEqual(0, segmentList.SelectedItems.Count);
                for (var i = 0; i < 100; i++)
                {
                    using var state = JsonDocument.Parse(await preview.CoreWebView2.ExecuteScriptAsync("Array.from(document.querySelectorAll('video')).every(v=>v.readyState>=2)&&Array.from(document.querySelectorAll('img')).every(v=>v.complete)"));
                    if (state.RootElement.GetBoolean()) break; await Task.Delay(50);
                }
                await preview.SeekAsync(1);
                Assert.AreEqual("\"Проверяем субтитры и предпросмотр.\"", await preview.CoreWebView2.ExecuteScriptAsync("document.getElementById('sub').textContent"));
                selectionTimeline.SelectItems([firstSubtitle.Item]);
                var positions = new List<double>(); preview.PositionChanged += positions.Add;
                var play = Descendants(editor).OfType<System.Windows.Controls.Button>().Single(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "VideoEditor.Play");
                play.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); await Task.Delay(600);
                var script = Descendants(editor).OfType<System.Windows.Controls.TextBox>().Single(t => System.Windows.Automation.AutomationProperties.GetAutomationId(t) == "VideoEditor.Script");
                Assert.IsTrue(script.SelectionLength > 0, "Automatically linked media must not disable word following for one explicit text fragment");
                using var audioState = JsonDocument.Parse(await preview.CoreWebView2.ExecuteScriptAsync("window.lopata.diagnostics()"));
                Assert.IsTrue(audioState.RootElement.GetProperty("audioRms").GetDouble() > .01, "Actual preview audio samples must reach the browser output graph");
                var baselineRms = audioState.RootElement.GetProperty("audioRms").GetDouble();
                var volumeTimeline = Descendants(editor).OfType<VideoTimeline>().Single();
                async Task ClickVolume(System.Windows.Point point, bool clipOnly) {
                    Assert.IsTrue(volumeTimeline.VolumePointer(point, clipOnly, true)); Assert.IsFalse(editor.VolumePopup?.IsOpen == true, "Pressing alone must not open the popup");
                    Assert.IsTrue(volumeTimeline.VolumePointer(point, clipOnly, false));
                    for (var wait = 0; wait < 40 && editor.VolumePopup?.IsOpen != true; wait++) await Task.Delay(25);
                    Assert.IsTrue(editor.VolumePopup?.IsOpen == true, "The popup must remain after releasing the initiating click");
                }
                await ClickVolume(new System.Windows.Point(60, 135), false);
                Assert.IsNotNull(editor.VolumePopup); Assert.AreEqual(200, editor.VolumePopup.Level.Maximum); editor.VolumePopup.Level.Value = 50; await Task.Delay(140);
                using (var half = JsonDocument.Parse(await preview.CoreWebView2.ExecuteScriptAsync("window.lopata.diagnostics()"))) Assert.AreEqual(.5, half.RootElement.GetProperty("audioRms").GetDouble() / baselineRms, .1);
                Assert.AreEqual(.5, p.Tracks[1].Volume); Assert.AreEqual(1, p.Tracks[1].Clips[0].Volume);
                editor.VolumePopup.IsOpen = false;
                await ClickVolume(new System.Windows.Point(200, 110), true);
                Assert.IsNotNull(editor.VolumePopup); editor.VolumePopup.Level.Value = 200; await Task.Delay(140);
                using (var doubled = JsonDocument.Parse(await preview.CoreWebView2.ExecuteScriptAsync("window.lopata.diagnostics()"))) Assert.AreEqual(1, doubled.RootElement.GetProperty("audioRms").GetDouble() / baselineRms, .12);
                Assert.AreEqual(.5, p.Tracks[1].Volume); Assert.AreEqual(2, p.Tracks[1].Clips[0].Volume);
                editor.VolumePopup.PercentInput.Text = "201"; Assert.IsFalse(editor.VolumePopup.TryApplyText()); Assert.AreEqual(2, p.Tracks[1].Clips[0].Volume);
                editor.VolumePopup.Level.Value = 0; await Task.Delay(100);
                using (var silent = JsonDocument.Parse(await preview.CoreWebView2.ExecuteScriptAsync("window.lopata.diagnostics()"))) Assert.IsTrue(silent.RootElement.GetProperty("audioRms").GetDouble() < .0001);
                editor.VolumePopup.Reset.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Assert.AreEqual(1, p.Tracks[1].Clips[0].Volume);
                var volumeEvidence = Environment.GetEnvironmentVariable("LOPATA_VIDEO_UI_EVIDENCE") ?? directory; Directory.CreateDirectory(volumeEvidence);
                if (editor.VolumePopup.Child is FrameworkElement volumePanel) { volumePanel.UpdateLayout(); var shot = new RenderTargetBitmap((int)Math.Ceiling(volumePanel.ActualWidth), (int)Math.Ceiling(volumePanel.ActualHeight), 96, 96, PixelFormats.Pbgra32); shot.Render(volumePanel); var encoded = new PngBitmapEncoder(); encoded.Frames.Add(BitmapFrame.Create(shot)); using var file = File.Create(Path.Combine(volumeEvidence, "volume-popup.png")); encoded.Save(file); }
                editor.VolumePopup.IsOpen = false; await Task.Delay(100); var volumeSaved = VideoProjectStore.Load(VideoProjectStore.Recovery(editor.ProjectId)); Assert.AreEqual(.5, volumeSaved.Tracks[1].Volume); Assert.AreEqual(1, volumeSaved.Tracks[1].Clips[0].Volume);
                await preview.PlayAsync(false);
                Assert.IsTrue(positions.Any(p => p > 1.2), "The actual browser clock must advance");
                var errors = await preview.CoreWebView2.ExecuteScriptAsync("Array.from(document.querySelectorAll('video,audio')).map(v=>v.error?.message||'').filter(Boolean)"); Assert.AreEqual("[]", errors);
                await preview.SeekAsync(1); await Task.Delay(200);
                var evidence = Environment.GetEnvironmentVariable("LOPATA_VIDEO_UI_EVIDENCE") ?? directory; Directory.CreateDirectory(evidence);
                await using (var file = File.Create(Path.Combine(evidence, "video-preview.png"))) await preview.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
                var bitmap = new RenderTargetBitmap((int)editor.ActualWidth, (int)editor.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(editor); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using (var file = File.Create(Path.Combine(evidence, "video-editor-layout.png"))) png.Save(file);
                // During playback without narration the caption correctly says that no narrator is audible.
                // Pause through the editor before checking the common profile used for future generation.
                play.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); await Task.Delay(80);
                var narrator = Descendants(editor).OfType<TextBlock>().Single(t => System.Windows.Automation.AutomationProperties.GetAutomationId(t) == "VideoEditor.SelectedNarrator");
                Assert.IsTrue(narrator.Text.Contains("Kokoro", StringComparison.Ordinal));
                narrator.BringIntoView(); await Task.Delay(80);
                var voiceShot = new RenderTargetBitmap((int)editor.ActualWidth, (int)editor.ActualHeight, 96, 96, PixelFormats.Pbgra32); voiceShot.Render(editor);
                var voicePng = new PngBitmapEncoder(); voicePng.Frames.Add(BitmapFrame.Create(voiceShot)); using (var file = File.Create(Path.Combine(evidence, "selected-narrator.png"))) voicePng.Save(file);
                await File.WriteAllTextAsync(Path.Combine(evidence, "preview-state.json"), await preview.CoreWebView2.ExecuteScriptAsync("JSON.stringify(Array.from(document.querySelectorAll('video,img')).map(v=>({tag:v.tagName,width:v.clientWidth,height:v.clientHeight,ready:v.readyState,src:v.currentSrc||v.src})))"));
                // Drive the actual embedded browser's pointer/wheel events, without desktop input.
                p.Width = 180; p.Height = 320; await preview.LoadAsync(p, 1, false);
                var timeline = Descendants(editor).OfType<VideoTimeline>().Single(); var clip = p.Tracks[0].Clips[0];
                var linked = Descendants(editor).OfType<System.Windows.Controls.CheckBox>().Single(c => System.Windows.Automation.AutomationProperties.GetAutomationId(c) == "VideoEditor.LinkedSelection");
                linked.IsChecked = false; linked.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                timeline.SelectedClip = clip.Id; timeline.SelectedTrack = p.Tracks[0].Id;
                var framing = Descendants(editor).OfType<System.Windows.Controls.CheckBox>().Single(c => System.Windows.Automation.AutomationProperties.GetAutomationId(c) == "VideoEditor.FrameMode");
                framing.IsChecked = true; framing.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); await Task.Delay(200);
                using var rect = JsonDocument.Parse(await preview.CoreWebView2.ExecuteScriptAsync("(()=>{const r=document.getElementById('stage').getBoundingClientRect();return {x:r.left+r.width/2,y:r.top+r.height/2,width:r.width}})()"));
                var x = rect.RootElement.GetProperty("x").GetDouble(); var y = rect.RootElement.GetProperty("y").GetDouble();
                async Task Pointer(string type, double px, double py, string button) => await preview.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type, x = px, y = py, button, clickCount = 1 }));
                await Pointer("mousePressed", x, y, "left"); await Pointer("mouseMoved", x + 20, y + 10, "left"); await Pointer("mouseReleased", x + 20, y + 10, "left"); await Task.Delay(200);
                Assert.AreEqual(20 * p.Width / rect.RootElement.GetProperty("width").GetDouble(), clip.X, .05, "Dragging must update the selected clip in project coordinates");
                await preview.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type = "mouseWheel", x, y, deltaX = 0, deltaY = -100 })); await Task.Delay(1500);
                Assert.AreEqual(1.12, clip.Scale, .001, "Wheel must zoom the selected clip");
                var saved = VideoProjectStore.Load(VideoProjectStore.Recovery(editor.ProjectId)); Assert.AreEqual(clip.X, saved.Tracks[0].Clips[0].X, .001); Assert.AreEqual(clip.Scale, saved.Tracks[0].Clips[0].Scale);
                var geometry = VideoFraming.Geometry(p, asset, clip); using var renderedWidth = JsonDocument.Parse(await preview.CoreWebView2.ExecuteScriptAsync("parseFloat(document.querySelector('video').parentElement.style.width)"));
                Assert.AreEqual(geometry.Width, renderedWidth.RootElement.GetInt32(), "Preview and encoder must use identical scaled widths");
                await using (var file = File.Create(Path.Combine(evidence, "portrait-framing.png"))) await preview.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
                await ClickVolume(new System.Windows.Point(200, 110), true);
                Assert.IsNotNull(editor.VolumePopup); editor.VolumePopup.PercentInput.Text = "75";
                var closingProject = editor.ProjectId; editor.Close(); editor = null;
                Assert.AreEqual(.75, VideoProjectStore.Load(VideoProjectStore.Recovery(closingProject)).Tracks[1].Clips[0].Volume, "Closing the editor must commit a valid typed percentage even without Enter");
            }
            finally { editor?.Close(); owner.Close(); ComponentLicenseGate.ConfirmAsync = gate; }
        });
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    { yield return root; foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) foreach (var descendant in Descendants(child)) yield return descendant; }
    private static Task OnUi(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher)); dispatcher.BeginInvoke(async () => { try { await action(); done.TrySetResult(); } catch (Exception error) { done.TrySetException(error); } finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } }); Dispatcher.Run(); }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task;
    }
}
