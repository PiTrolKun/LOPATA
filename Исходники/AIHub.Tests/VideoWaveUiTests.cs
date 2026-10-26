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
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Slider = System.Windows.Controls.Slider;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoWaveUiTests
{
    [TestMethod]
    public async Task DetailedTailRendersAndSpacingToggleRestoresUnlessEdited()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => {
            var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () => {
                var directory = Path.Combine(Path.GetTempPath(), "lopata-wave-ui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
                using var audit = ComponentAudit.UseTestStore(Path.Combine(directory, "audit"), false); var gate = ComponentLicenseGate.ConfirmAsync; ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
                var owner = new Window { Width = 100, Height = 100, ShowActivated = false, ShowInTaskbar = false };
                owner.Resources["WindowBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(14, 22, 38)); owner.Resources["TextPrimaryBrush"] = Brushes.WhiteSmoke; owner.Resources["LineBrush"] = Brushes.SlateGray;
                owner.Resources["PanelBrush"] = new SolidColorBrush(Color.FromRgb(23, 32, 49)); owner.Resources["SecondaryButtonBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(16, 24, 40)); owner.Resources["AccentBrush"] = Brushes.RoyalBlue; owner.Resources["UiBodyFontSize"] = 14d;
                var local = new LocalizationService(); local.Load("ru"); VideoEditorWindow? editor = null;
                try {
                    var waveFile = Path.Combine(directory, "bursts.wav"); VideoWaveformTests.WriteBursts(waveFile);
                    var videoFile = Path.Combine(directory, "source.mp4"); await VideoRuntime.RunAsync(false, ["-v", "error", "-n", "-f", "lavfi", "-i", "color=c=blue:s=320x180:r=30:d=6", "-i", waveFile, "-c:v", "libopenh264", "-g", "30", "-b:v", "400k", "-c:a", "aac", videoFile], CancellationToken.None);
                    var p = new VideoProject { Width = 320, Height = 180, Name = "Волна и серии" }; var asset = await VideoImport.ReadAsync(videoFile, directory, CancellationToken.None); VideoImport.Insert(p, asset, 0);
                    VideoEventCuts.Apply(p, p.Tracks[0].Clips[0].Id, [new() { Start = 0, End = 2 }, new() { Start = 2, End = 4 }, new() { Start = 4, End = 6 }]);
                    var voice = await VideoImport.ReadAsync(waveFile, directory, CancellationToken.None); p.Assets.Add(voice); var speech = new VideoTrack { Kind = "audio", Name = "Озвучка" }; p.Tracks.Add(speech);
                    foreach (var n in p.Narration) { n.Text = "Проверка фрагмента"; n.AudioAssetId = voice.Id; speech.Clips.Add(new() { AssetId = voice.Id, NarrationId = n.Id, Start = n.Start, Duration = 3, PlannedDuration = 2, SeriesNumbers = [.. n.SeriesNumbers] }); }
                    var fullWave = await VideoImport.ReadAsync(waveFile, directory, CancellationToken.None); p.Assets.Add(fullWave);
                    p.Tracks.Add(new() { Kind = "audio", Name = "Волна", Clips = [new() { AssetId = fullWave.Id, Duration = 6 }] });
                    owner.Show(); editor = new(owner, local.T, "ru", new VideoWork(), p) { ConfirmSaveOnClose = () => VideoCloseChoice.Discard, Width = 1440, Height = 980, ShowActivated = false }; editor.Show(); await editor.Ready.Task.WaitAsync(TimeSpan.FromSeconds(60));
                    var timeline = Descendants(editor).OfType<VideoTimeline>().Single(); var slider = Descendants(editor).OfType<Slider>().Single(c => Id(c) == "VideoEditor.Zoom"); slider.Value = 180;
                    editor.UpdateLayout(); await Task.Delay(80);
                    var before = Times(timeline.Project); var toggle = Descendants(editor).OfType<CheckBox>().Single(c => Id(c) == "VideoEditor.SeparateSeries");
                    toggle.IsChecked = true; toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); await Task.Delay(100);
                    Assert.IsTrue(timeline.Project.Tracks[0].Clips.Single(c => c.SeriesNumbers.Contains(2)).Start > 3);
                    timeline.SelectItems([new(timeline.Project.Tracks[0].Clips[0].Id)]); slider.Value = 170;
                    toggle.IsChecked = false; toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); await Task.Delay(100); Assert.AreEqual(before, Times(timeline.Project), "Selection and zoom must not commit the temporary layout");
                    slider.Value = 180; editor.UpdateLayout();
                    var bitmap = new RenderTargetBitmap((int)editor.ActualWidth, (int)editor.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(editor);
                    var at = timeline.TranslatePoint(new(145 + 5.792 * 180, 30 + 3 * 65 + 42), editor); var pixels = new byte[(int)bitmap.Width * (int)bitmap.Height * 4]; bitmap.CopyPixels(pixels, (int)bitmap.Width * 4, 0);
                    var bright = 0; for (var y = (int)at.Y - 16; y <= at.Y + 16; y++) for (var x = (int)at.X - 2; x <= at.X + 2; x++) if (x >= 0 && y >= 0 && x < bitmap.Width && y < bitmap.Height) { var b = (y * (int)bitmap.Width + x) * 4; if (pixels[b + 1] > 220 && pixels[b] > 150 && pixels[b + 2] > 60) bright++; }
                    Assert.IsTrue(bright > 0, "The short tail burst must render past the old 500-pixel boundary");
                    var evidence = Environment.GetEnvironmentVariable("LOPATA_VIDEO_WAVE_EVIDENCE") ?? directory; Directory.CreateDirectory(evidence);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var file = File.Create(Path.Combine(evidence, "waveform-layout.png"))) encoder.Save(file);
                    toggle.IsChecked = true; toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); await Task.Delay(100);
                    var spaced = Times(timeline.Project); Descendants(editor).OfType<Button>().Single(b => Id(b) == "VideoEditor.AddAudio").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); await Task.Delay(100);
                    toggle.IsChecked = false; toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); await Task.Delay(100); Assert.AreEqual(spaced, Times(timeline.Project));
                    var undo = Descendants(editor).OfType<Button>().Single(b => Id(b) == "VideoEditor.Undo"); for (var i = 0; i < 2; i++) { undo.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); await Task.Delay(100); }
                    Assert.AreEqual(before, Times(timeline.Project)); await File.WriteAllTextAsync(Path.Combine(evidence, "spacing-ui.json"), JsonSerializer.Serialize(new { UntouchedRestores = true, EditedKeepsArrangement = true, UndoRestores = true, TailPixels = bright })); done.TrySetResult();
                }
                catch (Exception error) { done.TrySetException(error); }
                finally { editor?.Close(); owner.Close(); ComponentLicenseGate.ConfirmAsync = gate; dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }); Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); await done.Task;
    }
    private static string Times(VideoProject p) => JsonSerializer.Serialize(p.Tracks.SelectMany(t => t.Clips).Select(c => (c.Id, c.Start)).Concat(p.Narration.Select(n => (n.Id, n.Start))), new JsonSerializerOptions { IncludeFields = true });
    private static string Id(DependencyObject item) => System.Windows.Automation.AutomationProperties.GetAutomationId(item);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject item) { for (var i = 0; i < VisualTreeHelper.GetChildrenCount(item); i++) { var child = VisualTreeHelper.GetChild(item, i); yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
}
