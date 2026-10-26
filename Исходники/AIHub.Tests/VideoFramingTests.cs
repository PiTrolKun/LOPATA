using System.IO;
using System.Security.Cryptography;
using AIHub.Services;
using AIHub.Services.VideoEditor;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoFramingTests
{
    [TestMethod]
    public async Task ExplicitUserRecordingCanExportThreePortraitFragmentsWithoutChangingTheOriginal()
    {
        var source = Environment.GetEnvironmentVariable("LOPATA_VIDEO_USER_RECORDING");
        if (string.IsNullOrWhiteSpace(source)) Assert.Inconclusive("Explicit user-authorized recording required.");
        var root = Path.Combine(Environment.GetEnvironmentVariable("LOPATA_VIDEO_FRAMING_EVIDENCE") ?? Path.GetTempPath(), "Recording-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        using var policy = ComponentAudit.UseTestStore(Path.Combine(root, "audit"), false);
        var gate = ComponentLicenseGate.ConfirmAsync; ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
        try {
            var before = SHA256.HashData(await File.ReadAllBytesAsync(source!));
            await VideoRuntime.PrepareAsync(CancellationToken.None); var a = await VideoImport.ReadAsync(source!, root, CancellationToken.None);
            var p = new VideoProject { Name = "Три области записи — вертикальный кадр", Width = 1080, Height = 1920, Fps = 15 };
            VideoImport.Insert(p, a, 0); foreach (var c in p.Tracks.SelectMany(t => t.Clips)) { c.Duration = 3; c.SourceIn = Math.Min(5, Math.Max(0, a.Duration - 3)); }
            var first = p.Tracks[0].Clips[0]; first.Scale = VideoFraming.FillScale(p, a, first);
            var offset = a.Width / 3d * p.Height / a.Height; first.X = offset;
            VideoEditing.Split(p, first.Id, 1); VideoEditing.Split(p, p.Tracks[0].Clips[1].Id, 2);
            p.Tracks[0].Clips[1].X = 0; p.Tracks[0].Clips[2].X = -offset;
            VideoProjectStore.Save(p, Path.Combine(root, "recording.lopataVideo"));
            var output = Path.Combine(root, "recording-1080x1920.mp4");
            await VideoExport.RunAsync(p, output, new(1080, 1920, 15, 0, 3, false, false), Guid.NewGuid().ToString("N"), null, CancellationToken.None);
            using var probe = await VideoRuntime.ProbeAsync(output, CancellationToken.None);
            var video = probe.RootElement.GetProperty("streams").EnumerateArray().Single(s => s.GetProperty("codec_type").GetString() == "video");
            Assert.AreEqual(1080, video.GetProperty("width").GetInt32()); Assert.AreEqual(1920, video.GetProperty("height").GetInt32());
            CollectionAssert.AreEqual(before, SHA256.HashData(await File.ReadAllBytesAsync(source!)));
            for (var i = 0; i < 3; i++) await VideoRuntime.RunAsync(false, ["-v", "error", "-n", "-ss", VideoExport.N(i + .5), "-i", output, "-frames:v", "1", Path.Combine(root, "region-" + i + ".png")], CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(root, "verification.json"), System.Text.Json.JsonSerializer.Serialize(new { Source = source, Sha256 = Convert.ToHexString(before), OriginalUnchanged = true, Output = output, Width = 1080, Height = 1920, SourceAssets = p.Assets.Count, Fragments = p.Tracks[0].Clips.Count, Scales = p.Tracks[0].Clips.Select(c => c.Scale).ToArray(), Offsets = p.Tracks[0].Clips.Select(c => c.X).ToArray() }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        finally { ComponentLicenseGate.ConfirmAsync = gate; }
    }
    [TestMethod]
    public async Task SplitLandscapeIntoIndependentPortraitViewsAndExportTheChosenRegions()
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-portrait-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        using var policy = ComponentAudit.UseTestStore(Path.Combine(root, "audit"), false);
        var gate = ComponentLicenseGate.ConfirmAsync; ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
        try {
            await VideoRuntime.PrepareAsync(CancellationToken.None);
            var bmp = Path.Combine(root, "three-panels.bmp");
            using (var writer = new BinaryWriter(File.Create(bmp))) {
                writer.Write((short)0x4d42); writer.Write(54 + 960 * 540 * 3); writer.Write(0); writer.Write(54); writer.Write(40); writer.Write(960); writer.Write(540);
                writer.Write((short)1); writer.Write((short)24); writer.Write(new byte[24]);
                for (var y = 0; y < 540; y++) for (var x = 0; x < 960; x++) { writer.Write((byte)(x >= 640 ? 255 : 0)); writer.Write((byte)(x >= 320 && x < 640 ? 255 : 0)); writer.Write((byte)(x < 320 ? 255 : 0)); }
            }
            var source = Path.Combine(root, "landscape.mp4");
            await VideoRuntime.RunAsync(false, ["-v", "error", "-n", "-loop", "1", "-i", bmp, "-t", "3", "-r", "12", "-c:v", "libopenh264", "-b:v", "2M", source], CancellationToken.None);
            var hash = SHA256.HashData(await File.ReadAllBytesAsync(source));
            var p = new VideoProject { Width = 180, Height = 320, Fps = 12 };
            var a = await VideoImport.ReadAsync(source, root, CancellationToken.None); VideoImport.Insert(p, a, 0);
            var first = p.Tracks[0].Clips[0]; first.Scale = VideoFraming.FillScale(p, a, first); first.X = 320d / 540 * 320;
            var originalScale = first.Scale; var originalX = first.X;
            VideoEditing.Split(p, first.Id, 1); var second = p.Tracks[0].Clips[1];
            Assert.AreEqual(originalScale, second.Scale); Assert.AreEqual(originalX, second.X);
            VideoEditing.Split(p, second.Id, 2); var third = p.Tracks[0].Clips[2];
            second.X = 0; third.X = -originalX;
            Assert.AreEqual(originalX, first.X); Assert.AreEqual(originalScale, first.Scale);
            var projectPath = Path.Combine(root, "portrait.lopataVideo"); VideoProjectStore.Save(p, projectPath); p = VideoProjectStore.Load(projectPath);
            Assert.HasCount(1, p.Assets); Assert.AreEqual(source, p.Assets[0].Path); Assert.AreEqual(-originalX, p.Tracks[0].Clips[2].X);
            var output = Path.Combine(root, "portrait.mp4"); await VideoExport.RunAsync(p, output, new(180, 320, 12, 0, 3, false, false), Guid.NewGuid().ToString("N"), null, CancellationToken.None);
            for (var panel = 0; panel < 3; panel++) {
                var frame = Path.Combine(root, panel + ".rgba"); await VideoRuntime.RunAsync(false, ["-v", "error", "-n", "-ss", VideoExport.N(panel + .4), "-i", output, "-frames:v", "1", "-pix_fmt", "rgba", "-f", "rawvideo", frame], CancellationToken.None);
                var bytes = await File.ReadAllBytesAsync(frame);
                foreach (var y in new[] { 2, 160, 317 }) {
                    var pixel = (y * 180 + 90) * 4; Assert.IsTrue(bytes[pixel + panel] > 180, $"Fragment {panel}: the chosen region must fill the portrait frame");
                    for (var channel = 0; channel < 3; channel++) if (channel != panel) Assert.IsTrue(bytes[pixel + channel] < 60);
                }
            }
            CollectionAssert.AreEqual(hash, SHA256.HashData(await File.ReadAllBytesAsync(source)));
            var evidence = Environment.GetEnvironmentVariable("LOPATA_VIDEO_FRAMING_EVIDENCE");
            if (!string.IsNullOrWhiteSpace(evidence)) { Directory.CreateDirectory(evidence); File.Copy(output, Path.Combine(evidence, "three-regions-portrait.mp4"), true); File.Copy(projectPath, Path.Combine(evidence, "three-regions.lopataVideo"), true); }
        }
        finally { ComponentLicenseGate.ConfirmAsync = gate; }
    }
}
