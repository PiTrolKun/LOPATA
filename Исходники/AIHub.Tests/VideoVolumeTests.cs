using System.IO;
using System.Security.Cryptography;
using AIHub.Services;
using AIHub.Services.VideoEditor;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoVolumeTests
{
    [TestMethod]
    public void TrackAndClipLevelsAreIndependentBoundedAndUndoable()
    {
        var p = new VideoProject(); var track = p.Tracks.Single(t => t.Kind == "audio");
        var asset = new VideoAsset { Path = Path.GetFullPath("voice.wav"), Kind = "audio", Duration = 10, AudioStream = 0 }; p.Assets.Add(asset);
        track.Clips.AddRange([new() { AssetId = asset.Id, Duration = 5 }, new() { AssetId = asset.Id, Start = 5, SourceIn = 5, Duration = 5 }]);
        var edit = new VideoEditing(p); var first = track.Clips[0].Id;
        edit.Edit(x => VideoAudioVolume.Set(x, track.Id, null, 50)); edit.Edit(x => VideoAudioVolume.Set(x, track.Id, first, 200));
        Assert.AreEqual(.5, track.Volume); Assert.AreEqual(2, track.Clips[0].Volume); Assert.AreEqual(1, track.Clips[1].Volume);
        edit.Undo(); Assert.AreEqual(.5, edit.Project.Tracks[1].Volume); Assert.AreEqual(1, edit.Project.Tracks[1].Clips[0].Volume); edit.Redo(); Assert.AreEqual(2, edit.Project.Tracks[1].Clips[0].Volume);
        foreach (var bad in new[] { -1d, 201, double.NaN, double.PositiveInfinity }) Assert.ThrowsExactly<InvalidDataException>(() => VideoAudioVolume.Set(edit.Project, track.Id, first, bad));
        edit.Project.Tracks[1].Locked = true; Assert.ThrowsExactly<InvalidOperationException>(() => VideoAudioVolume.Set(edit.Project, track.Id, null, 0));
    }
    [TestMethod]
    public async Task ExportedAudioHonorsTrackAndIndividualClipLevelsIncludingZeroAnd200Percent()
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-volume-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        using var policy = ComponentAudit.UseTestStore(Path.Combine(root, "audit"), false);
        var gate = ComponentLicenseGate.ConfirmAsync; ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
        try {
            var source = Path.Combine(root, "tone.wav");
            using (var writer = new BinaryWriter(File.Create(source))) {
                const int count = 48000 * 2; writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + count * 2); writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(48000); writer.Write(96000); writer.Write((short)2); writer.Write((short)16); writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(count * 2);
                for (var i = 0; i < count; i++) writer.Write((short)(Math.Sin(i * 2 * Math.PI * 440 / 48000) * 3000));
            }
            var hash = SHA256.HashData(await File.ReadAllBytesAsync(source)); await VideoRuntime.PrepareAsync(CancellationToken.None);
            var p = new VideoProject { Width = 160, Height = 90, Fps = 12 }; var a = await VideoImport.ReadAsync(source, root, CancellationToken.None); VideoImport.Insert(p, a, 0);
            var track = p.Tracks.Single(t => t.Kind == "audio"); VideoEditing.Split(p, track.Clips[0].Id, 1);
            async Task<float[]> Render(string name) {
                var output = Path.Combine(root, name + ".mp4"); await VideoExport.RunAsync(p, output, new(160, 90, 12, 0, 2, false, false), Guid.NewGuid().ToString("N"), null, CancellationToken.None);
                var raw = Path.Combine(root, name + ".f32"); await VideoRuntime.RunAsync(false, ["-v", "error", "-n", "-i", output, "-map", "0:a:0", "-ac", "1", "-ar", "48000", "-f", "f32le", raw], CancellationToken.None);
                var bytes = await File.ReadAllBytesAsync(raw); var samples = new float[bytes.Length / 4]; Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length); return samples;
            }
            static double Rms(float[] samples, int fragment) => Math.Sqrt(samples.Skip(fragment * 48000 + 12000).Take(24000).Average(x => (double)x * x));
            var baseline = await Render("baseline");
            VideoAudioVolume.Set(p, track.Id, null, 50); VideoAudioVolume.Set(p, track.Id, track.Clips[0].Id, 200);
            var mixed = await Render("independent"); Assert.AreEqual(1, Rms(mixed, 0) / Rms(baseline, 0), .12); Assert.AreEqual(.5, Rms(mixed, 1) / Rms(baseline, 1), .08);
            VideoAudioVolume.Set(p, track.Id, null, 200); VideoAudioVolume.Set(p, track.Id, track.Clips[0].Id, 0);
            var muted = await Render("zero-and-200"); Assert.IsTrue(Rms(muted, 0) < .0001); Assert.AreEqual(2, Rms(muted, 1) / Rms(baseline, 1), .16);
            var saved = Path.Combine(root, "levels.lopataVideo"); VideoProjectStore.Save(p, saved); var reopened = VideoProjectStore.Load(saved); Assert.AreEqual(2, reopened.Tracks[1].Volume); Assert.AreEqual(0, reopened.Tracks[1].Clips[0].Volume); Assert.AreEqual(1, reopened.Tracks[1].Clips[1].Volume);
            CollectionAssert.AreEqual(hash, SHA256.HashData(await File.ReadAllBytesAsync(source)));
        }
        finally { ComponentLicenseGate.ConfirmAsync = gate; }
    }
}
