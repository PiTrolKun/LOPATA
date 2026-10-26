using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Controls.VideoEditor;
using AIHub.Services;
using AIHub.Services.VideoEditor;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoVoiceSelectionUiTests
{
    [TestMethod]
    public async Task EditorCaptionSeparatesTheCommonChoiceFromTheRecordingDuringSelectionAndPlayback()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(() => {
                var owner = new Window { ShowActivated = false, Width = 100, Height = 100, WindowStyle = WindowStyle.ToolWindow }; VideoEditorWindow? editor = null;
                try
                {
                    foreach (var key in new[] { "WindowBackgroundBrush", "PanelBrush", "SecondaryButtonBackgroundBrush" }) owner.Resources[key] = Brushes.Black;
                    foreach (var key in new[] { "TextPrimaryBrush", "TextSecondaryBrush", "LineBrush", "AccentBrush" }) owner.Resources[key] = Brushes.White;
                    owner.Resources["UiBodyFontSize"] = 14d;
                    var project = VideoVoiceSelectionTests.TenFragments();
                    var recordingId = Guid.NewGuid().ToString("N");
                    var recorded = new VideoSpeechProfile("kokoro-sveta", "ru", 100, new());
                    project.Assets.Add(new() { Id = recordingId, Path = Path.Combine(Path.GetTempPath(), "voice-selection-ui.wav"), Kind = "audio", Duration = 10, NarratorProfile = recorded });
                    project.Tracks.Add(new() { Kind = "audio", Clips = [new() { AssetId = recordingId, Duration = 10 }] });
                    foreach (var n in project.Narration) n.AudioAssetId = recordingId;
                    project.Narration[0].Provider = "silero56";
                    var l = new LocalizationService(); l.Load("ru");
                    owner.Show();
                    editor = new(owner, l.T, "ru", new VideoWork(), project) { ConfirmSaveOnClose = () => VideoCloseChoice.Discard };
                    object Field(string key) => typeof(VideoEditorWindow).GetField(key, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
                    void Set(string key, object value) => typeof(VideoEditorWindow).GetField(key, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(editor, value);
                    void Update() => typeof(VideoEditorWindow).GetMethod("UpdatePosition", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, null);
                    var segments = (VideoSegmentList)Field("_segments"); segments.ItemsSource = project.Narration;
                    foreach (var n in project.Narration) segments.SelectedItems.Add(n);
                    ((System.Windows.Controls.CheckBox)Field("_follow")).IsChecked = false;
                    var label = (TextBlock)Field("_voiceSummary");
                    Set("_playing", true); Set("_position", .5); Update();
                    Assert.AreEqual("Звучит: Kokoro · Света", label.Text);
                    Set("_playing", false); Update(); Assert.AreEqual("Записано: Kokoro · Света", label.Text);
                    VideoVoiceProfiles.Apply(project, new("silero56", "ru", 100, new("xenia")), true, new HashSet<string>());
                    Update(); StringAssert.Contains(label.Text, "Общая озвучка: Silero");
                    Set("_playing", true); Update(); Assert.AreEqual("Звучит: Kokoro · Света", label.Text);
                    project.Assets[0].NarratorProfile = new("silero56", "ru", 100, new("xenia"));
                    foreach (var n in project.Narration) n.Stale = false;
                    Update(); StringAssert.Contains(label.Text, "Звучит: Silero");
                    Assert.IsFalse(label.Text.Contains("Разные"));
                    var evidence = Environment.GetEnvironmentVariable("LOPATA_VOICE_SELECTION_EVIDENCE");
                    if (!string.IsNullOrEmpty(evidence))
                    {
                        Directory.CreateDirectory(evidence);
                        label.Background = Brushes.White; label.Foreground = Brushes.Black; label.Measure(new(280, 44)); label.Arrange(new Rect(0, 0, 280, 44));
                        var image = new RenderTargetBitmap(280, 44, 96, 96, PixelFormats.Pbgra32); image.Render(label);
                        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
                        using var output = File.Create(Path.Combine(evidence, "actual-narrator-caption.png")); png.Save(output);
                    }
                    done.SetResult();
                }
                catch (Exception error) { done.SetException(error); }
                finally { editor?.Close(); owner.Close(); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
