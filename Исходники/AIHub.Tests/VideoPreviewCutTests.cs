using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using AIHub.Controls.VideoEditor;
using AIHub.Services;
using AIHub.Services.VideoEditor;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoPreviewCutTests
{
    [TestMethod]
    public async Task OriginalMp4AudioRemainsAudibleAcrossLinkedCutsAndSeeks()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => {
            var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () => {
                var directory = Path.Combine(Path.GetTempPath(), "lopata-preview-cuts-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
                using var audit = ComponentAudit.UseTestStore(Path.Combine(directory, "audit"), false); var gate = ComponentLicenseGate.ConfirmAsync; ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
                var preview = new VideoPreview(); var owner = new Window { Width = 640, Height = 400, Content = preview, ShowActivated = false, ShowInTaskbar = false };
                try {
                    var source = Path.Combine(directory, "tone-video.mp4");
                    var tone = Path.Combine(directory, "tone.wav");
                    using (var writer = new BinaryWriter(File.Create(tone))) { const int samples = 48000 * 4; writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples * 2); writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(48000); writer.Write(96000); writer.Write((short)2); writer.Write((short)16); writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(samples * 2); for (var i = 0; i < samples; i++) writer.Write((short)(Math.Sin(i * 2 * Math.PI * 440 / 48000) * 6000)); }
                    await VideoRuntime.RunAsync(false, ["-v", "error", "-n", "-f", "lavfi", "-i", "color=c=blue:s=320x180:r=30:d=4", "-i", tone, "-c:v", "libopenh264", "-g", "30", "-b:v", "400k", "-c:a", "aac", source], CancellationToken.None);
                    var p = new VideoProject { Width = 320, Height = 180 }; var asset = await VideoImport.ReadAsync(source, directory, CancellationToken.None); VideoImport.Insert(p, asset, 0);
                    VideoEditing.Split(p, p.Tracks[0].Clips[0].Id, 1); VideoEditing.Split(p, p.Tracks[0].Clips.OrderBy(c => c.Start).Last().Id, 2);
                    var fixture = Environment.GetEnvironmentVariable("LOPATA_VIDEO_CUT_FIXTURE");
                    if (!string.IsNullOrWhiteSpace(fixture)) {
                        p = VideoProjectStore.Load(fixture); p.Id = Guid.NewGuid().ToString("N"); p.Tracks.RemoveAll(t => t.Name == "Narration"); p.Narration.Clear();
                        foreach (var a in p.Assets.Where(a => a.Kind == "video" && a.AudioStream >= 0)) await VideoPreviewAssets.PrepareAudioAsync(p, a, CancellationToken.None);
                        if (Environment.GetEnvironmentVariable("LOPATA_VIDEO_CUT_EXPORT") == "1") {
                            var evidence = Environment.GetEnvironmentVariable("LOPATA_VIDEO_CUT_EVIDENCE") ?? directory; Directory.CreateDirectory(evidence);
                            var output = Path.Combine(evidence, "user-fragment-fixed.mp4");
                            await VideoExport.RunAsync(p, output, new(1920, 1080, 30, 0, 12, false, false), Guid.NewGuid().ToString("N"), null, CancellationToken.None);
                            using var keys = JsonDocument.Parse(await VideoRuntime.RunAsync(true, ["-v", "error", "-select_streams", "v:0", "-show_packets", "-show_entries", "packet=pts_time,flags", "-of", "json", output], CancellationToken.None));
                            var times = keys.RootElement.GetProperty("packets").EnumerateArray().Where(v => v.GetProperty("flags").GetString()!.Contains('K')).Select(v => double.Parse(v.GetProperty("pts_time").GetString()!, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                            Assert.IsTrue(times.Length >= 12); Assert.IsTrue(times.Zip(times.Skip(1).Append(12d), (a, b) => b - a).Max() <= 1.04);
                            await File.WriteAllTextAsync(Path.Combine(evidence, "user-export-keyframes.json"), JsonSerializer.Serialize(times));
                        }
                    }
                    else {
                        await VideoPreviewAssets.PrepareAsync(p, asset, CancellationToken.None);
                        var output = Path.Combine(directory, "seekable.mp4");
                        await VideoExport.RunAsync(p, output, new(320, 180, 30, 0, 4, false, false), Guid.NewGuid().ToString("N"), null, CancellationToken.None);
                        foreach (var path in new[] { asset.Proxy!, output }) {
                            using var packets = JsonDocument.Parse(await VideoRuntime.RunAsync(true, ["-v", "error", "-select_streams", "v:0", "-show_packets", "-show_entries", "packet=pts_time,flags", "-of", "json", path], CancellationToken.None));
                            var keys = packets.RootElement.GetProperty("packets").EnumerateArray().Where(v => v.GetProperty("flags").GetString()!.Contains('K')).Select(v => double.Parse(v.GetProperty("pts_time").GetString()!, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                            Assert.IsTrue(keys.Length >= 4, "Preview and export need regular keyframes");
                            Assert.IsTrue(keys.Zip(keys.Skip(1).Append(4d), (a, b) => b - a).Max() <= 1.04);
                            var evidence = Environment.GetEnvironmentVariable("LOPATA_VIDEO_CUT_EVIDENCE") ?? directory; Directory.CreateDirectory(evidence);
                            await File.WriteAllTextAsync(Path.Combine(evidence, Path.GetFileName(path) + "-keyframes.json"), JsonSerializer.Serialize(keys));
                        }
                    }
                    owner.Show(); await preview.InitializeAsync(CancellationToken.None); await preview.LoadAsync(p, .25, false);
                    for (var i = 0; i < 100; i++) { using var ready = JsonDocument.Parse(await preview.CoreWebView2.ExecuteScriptAsync("Array.from(document.querySelectorAll('video,audio')).every(m=>m.readyState>=2)")); if (ready.RootElement.GetBoolean()) break; await Task.Delay(40); }
                    await preview.PlayAsync(true);
                    var readings = new List<object>();
                    foreach (var at in string.IsNullOrWhiteSpace(fixture) ? new[] { .55, 1.35, 2.35, .4 } : new[] { 3d, 6d, 8d, 12d, 40d, 3d }) {
                        await preview.SeekAsync(at); await Task.Delay(260);
                        var json = await preview.CoreWebView2.ExecuteScriptAsync("({mix:window.lopata.diagnostics(),media:Array.from(document.querySelectorAll('audio')).map(m=>({time:m.currentTime,ready:m.readyState,paused:m.paused,error:m.error?.message||''}))})");
                        using var sample = JsonDocument.Parse(json); var rms = sample.RootElement.GetProperty("mix").GetProperty("audioRms").GetDouble(); readings.Add(new { At = at, Rms = rms, State = sample.RootElement.Clone() });
                        var evidence = Environment.GetEnvironmentVariable("LOPATA_VIDEO_CUT_EVIDENCE") ?? directory; Directory.CreateDirectory(evidence); await File.WriteAllTextAsync(Path.Combine(evidence, "preview-audio-cuts.json"), JsonSerializer.Serialize(readings, new JsonSerializerOptions { WriteIndented = true }));
                        Assert.IsTrue(rms > .01, "Original MP4 audio must remain audible at " + at + " s; " + json);
                    }
                    if (string.IsNullOrWhiteSpace(fixture)) {
                        await preview.SeekAsync(.7); await Task.Delay(650);
                        using var across = JsonDocument.Parse(await preview.CoreWebView2.ExecuteScriptAsync("window.lopata.diagnostics()"));
                        Assert.IsTrue(across.RootElement.GetProperty("audioRms").GetDouble() > .01, "Continuous playback across the first cut must retain original audio");
                    }
                    done.TrySetResult();
                }
                catch (Exception error) { done.TrySetException(error); }
                finally { preview.Dispose(); owner.Close(); ComponentLicenseGate.ConfirmAsync = gate; dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }); Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); await done.Task;
    }
}
