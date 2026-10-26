using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AIHub.Controls.VideoEditor;
using AIHub.Services;
using AIHub.Services.VideoEditor;
using Point = System.Windows.Point;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoAudioStretchTests
{
    [TestMethod]
    public async Task DragShortensPlaybackBySpeedAndKeepsFinalToneInPreviewAndExport()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => {
            var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () => {
                var directory = Path.Combine(Path.GetTempPath(), "lopata-audio-stretch-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
                using var audit = ComponentAudit.UseTestStore(Path.Combine(directory, "audit"), false); var gate = ComponentLicenseGate.ConfirmAsync; ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
                var preview = new VideoPreview(); var timeline = new VideoTimeline { SynchronizeSelection = false, Snap = false };
                var panel = new DockPanel(); DockPanel.SetDock(timeline, Dock.Bottom); panel.Children.Add(timeline); panel.Children.Add(preview);
                var window = new Window { Width = 800, Height = 650, Content = panel, ShowActivated = false, ShowInTaskbar = false };
                try {
                    var source = Path.Combine(directory, "two-tones.wav");
                    using (var writer = new BinaryWriter(File.Create(source))) { const int samples = 48000 * 6; writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples * 2); writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(48000); writer.Write(96000); writer.Write((short)2); writer.Write((short)16); writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(samples * 2); for (var i = 0; i < samples; i++) writer.Write((short)(Math.Sin(i * 2 * Math.PI * (i < samples / 2 ? 440 : 880) / 48000) * 6500)); }
                    var sourceHash = System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(source));
                    var p = new VideoProject { Width = 320, Height = 180, Fps = 30 };
                    var a = await VideoImport.ReadAsync(source, directory, CancellationToken.None); VideoImport.Insert(p, a, 0);
                    var clip = p.Tracks[1].Clips.Single(); var editing = new VideoEditing(p); timeline.Project = p; timeline.Refresh();
                    timeline.EditRequested += edit => { editing.Edit(edit); timeline.Project = editing.Project; timeline.Refresh(); };
                    window.Show(); window.UpdateLayout();
                    var block = timeline.Blocks().Single(); var edge = new Point(block.Bounds.Right - 2, block.Bounds.Top + 20);
                    timeline.SelectionDown(edge, ModifierKeys.None); timeline.SelectionMove(edge - new Vector(3 * timeline.Zoom, 0)); timeline.SelectionUp(edge - new Vector(3 * timeline.Zoom, 0));
                    Assert.AreEqual(3, clip.Duration); Assert.AreEqual(2, clip.Speed); Assert.AreEqual(0, clip.SourceIn); Assert.AreEqual(6, clip.Duration * clip.Speed);
                    // The left edge keeps the same source span, too, and Undo restores it.
                    block = timeline.Blocks().Single(); edge = new Point(block.Bounds.Left + 2, block.Bounds.Top + 20);
                    timeline.SelectionDown(edge, ModifierKeys.None); timeline.SelectionMove(edge + new Vector(timeline.Zoom, 0)); timeline.SelectionUp(edge + new Vector(timeline.Zoom, 0));
                    Assert.AreEqual(1, clip.Start); Assert.AreEqual(3, clip.Speed); Assert.AreEqual(6, clip.Duration * clip.Speed);
                    editing.Undo(); p = editing.Project; clip = p.Tracks[1].Clips.Single();
                    var saved = Path.Combine(directory, "stretched.lopataVideo"); VideoProjectStore.Save(p, saved); p = VideoProjectStore.Load(saved); clip = p.Tracks[1].Clips.Single(); Assert.AreEqual(2, clip.Speed);
                    await VideoPreviewAssets.PrepareAsync(p, p.Assets.Single(), CancellationToken.None); await preview.InitializeAsync(CancellationToken.None); await preview.LoadAsync(p, 0, false);
                    for (var i = 0; i < 100; i++) { if (await preview.CoreWebView2.ExecuteScriptAsync("document.querySelector('audio').readyState>=2") == "true") break; await Task.Delay(30); }
                    await preview.SeekAsync(2.2); await preview.PlayAsync(true); await Task.Delay(250);
                    using var state = JsonDocument.Parse(await preview.CoreWebView2.ExecuteScriptAsync("({rate:document.querySelector('audio').playbackRate,pitch:document.querySelector('audio').preservesPitch,sourceTime:document.querySelector('audio').currentTime,mix:window.lopata.diagnostics()})"));
                    Assert.AreEqual(2, state.RootElement.GetProperty("rate").GetDouble()); Assert.IsTrue(state.RootElement.GetProperty("pitch").GetBoolean()); Assert.IsTrue(state.RootElement.GetProperty("sourceTime").GetDouble() > 4.4); Assert.IsTrue(state.RootElement.GetProperty("mix").GetProperty("audioRms").GetDouble() > .01);
                    await preview.PlayAsync(false);
                    var output = Path.Combine(directory, "stretched.mp4"); await VideoExport.RunAsync(p, output, new(320, 180, 30, 0, 3, false, false), Guid.NewGuid().ToString("N"), null, CancellationToken.None);
                    var tail = Path.Combine(directory, "tail.raw"); await VideoRuntime.RunAsync(false, ["-v", "error", "-n", "-ss", "2.4", "-i", output, "-t", "0.3", "-vn", "-ar", "8000", "-ac", "1", "-c:a", "pcm_s16le", "-f", "s16le", tail], CancellationToken.None);
                    var bytes = await File.ReadAllBytesAsync(tail); var samplesOut = Enumerable.Range(0, bytes.Length / 2).Select(i => BitConverter.ToInt16(bytes, i * 2) / 32768d).ToArray();
                    double Power(int hz) { double real = 0, imag = 0; for (var i = 0; i < samplesOut.Length; i++) { real += samplesOut[i] * Math.Cos(2 * Math.PI * hz * i / 8000); imag += samplesOut[i] * Math.Sin(2 * Math.PI * hz * i / 8000); } return real * real + imag * imag; }
                    Assert.IsTrue(Power(880) > Power(440) * 4, "The exported tail must contain the final 880Hz tone at its original pitch, not a truncated first half");
                    CollectionAssert.AreEqual(sourceHash, System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(source)));
                    var normal = p.Clone(); normal.StretchAudio = false; editing = new VideoEditing(normal); timeline.Project = normal; timeline.Refresh();
                    block = timeline.Blocks().Single(); edge = new Point(block.Bounds.Right - 2, block.Bounds.Top + 20);
                    timeline.SelectionDown(edge, ModifierKeys.None); timeline.SelectionMove(edge - new Vector(timeline.Zoom, 0)); timeline.SelectionUp(edge - new Vector(timeline.Zoom, 0));
                    Assert.AreEqual(2, normal.Tracks[1].Clips[0].Duration); Assert.AreEqual(2, normal.Tracks[1].Clips[0].Speed, "Turning stretch off restores trimming without changing speed");
                    var evidence = Environment.GetEnvironmentVariable("LOPATA_VIDEO_SERIES_EVIDENCE") ?? directory; Directory.CreateDirectory(evidence);
                    await File.WriteAllTextAsync(Path.Combine(evidence, "stretch.json"), JsonSerializer.Serialize(new { SourceSeconds = 6, OutputSeconds = 3, Speed = clip.Speed, Preview = state.RootElement.Clone(), Tail880Power = Power(880), Tail440Power = Power(440), SourceUnchanged = true }, new JsonSerializerOptions { WriteIndented = true }));
                    done.TrySetResult();
                }
                catch (Exception error) { done.TrySetException(error); }
                finally { preview.Dispose(); window.Close(); ComponentLicenseGate.ConfirmAsync = gate; dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }); Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); await done.Task;
    }
}
