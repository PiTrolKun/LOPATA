using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VideoPreparation;
using SkiaSharp;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoPreparationFinishInstalledTests
{
    [TestMethod]
    public async Task InstalledAudioProbeMetadataAuditAndRetainedNativeFixture()
    {
        var parent = Environment.GetEnvironmentVariable("LOPATA_VIDEO_FINISH_ARTIFACTS");
        if (string.IsNullOrWhiteSpace(parent)) { Assert.Inconclusive("Opt-in installed FFmpeg check."); return; }
        var root = Path.Combine(Path.GetFullPath(parent), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(parent, "physical-root.txt"), root);
        var plain = Path.Combine(root, "tone.wav");
        using (var output = new BinaryWriter(File.Create(plain), Encoding.ASCII))
        {
            const int rate = 16000, count = rate * 5; output.Write("RIFF"u8); output.Write(36 + count * 2); output.Write("WAVEfmt "u8);
            output.Write(16); output.Write((short)1); output.Write((short)1); output.Write(rate); output.Write(rate * 2); output.Write((short)2); output.Write((short)16);
            output.Write("data"u8); output.Write(count * 2); for (var i = 0; i < count; i++) output.Write((short)(Math.Sin(i * 2 * Math.PI * 220 / rate) * 700));
        }
        var timing = new MusicTimedLyrics { JobId = Guid.NewGuid().ToString("N"), Variant = 0, SourceLyrics = "First\nFinal", LyricsHash = MusicTimedLyrics.HashLyrics("First\nFinal"),
            AudioPayloadHash = await MusicLyricsTimingMetadata.AudioPayloadHashAsync(plain, default), DurationSeconds = 5, Provider = "fixture", ProviderVersion = "1", Lines = [new(0,5,0,2), new(6,5,2,5)] };
        var timed = Path.Combine(root, "timed.wav"); await MusicLyricsTimingMetadata.WriteCopyAsync(plain, timed, timing, default);
        var hash = SHA256.HashData(File.ReadAllBytes(timed)); var log = new List<string>();
        VideoBoardAudioImport? imported = null;
        foreach (var enabled in new[] { false, false, true })
        {
            using var audit = ComponentAudit.UseTestStore(Path.Combine(root, "audit"), enabled); using var logs = ComponentAudit.CaptureLog(log.Add);
            imported = await VideoBoardAudioImport.ImportAsync(timed, Path.Combine(root, "media"), default);
            Assert.AreEqual(5, imported.Audio.Seconds, .001); Assert.AreEqual("First\nFinal", imported.Audio.TimedLyrics!.SourceLyrics);
            CollectionAssert.AreEqual(File.ReadAllBytes(timed), File.ReadAllBytes(imported.Path));
        }
        CollectionAssert.AreEqual(hash, SHA256.HashData(File.ReadAllBytes(timed)));
        var p = VideoPreparationFinishTests.ReadyProject(); p.Title = "Проверка раскадровки";
        p.Attachments.Add(new(imported!.Path, imported.Audio.Role)); p.Storyboard!.Audio = imported.Audio;
        using (var bitmap = new SKBitmap(480, 270))
        {
            using var canvas = new SKCanvas(bitmap); canvas.Clear(new SKColor(31, 52, 78));
            using var paint = new SKPaint { Color = new SKColor(232, 168, 78), IsAntialias = true }; canvas.DrawCircle(240,135,85,paint);
            paint.Color = SKColors.White; paint.StrokeWidth = 6; canvas.DrawLine(240,135,240,80,paint); canvas.DrawLine(240,135,288,160,paint);
            using var image = SKImage.FromBitmap(bitmap); using var bytes = image.Encode(SKEncodedImageFormat.Png, 100);
            var path = Path.Combine(root, "clock.png"); File.WriteAllBytes(path, bytes.ToArray());
            var file = await VideoReferenceFiles.ImportAsync(path, Path.Combine(root, "images")); p.References!.Images.Add(file.Image); p.Attachments.Add(new(file.Path, file.Image.AttachmentRole));
            VideoReferences.Bind(p, file.Image.Id, p.World!.Cards[0].Id, "appearance", "fixture");
            foreach (var frame in p.Storyboard.Frames) frame.ImageId = file.Image.Id;
        }
        p.Shots!.Source = VideoShots.Hash(VideoShots.Source(p)); VideoShots.Capture(p); VideoStoryboard.Synchronize(p); VideoStoryboard.Capture(p);
        Assert.IsTrue(VideoPreparationReadiness.Ready(p)); p.Validate();
        VideoPreparationArchive.Save(p, Path.Combine(root, "native.lopataAIVid"));
        var result = await VideoPreparationExport.ExportAsync(p, Path.Combine(root, "export"), default);
        File.WriteAllText(Path.Combine(root, "verification.json"), JsonSerializer.Serialize(new { result, p.Id, audit = log, duration = imported.Audio.Seconds, metadata = imported.Audio.TimedLyrics!.SourceLyrics }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
