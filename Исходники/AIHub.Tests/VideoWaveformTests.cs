using System.IO;
using AIHub.Services;
using AIHub.Services.VideoEditor;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoWaveformTests
{
    internal static void WriteBursts(string path)
    {
        using var writer = new BinaryWriter(File.Create(path)); const int samples = 48000 * 6;
        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples * 4); writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1); writer.Write((short)2); writer.Write(48000); writer.Write(192000); writer.Write((short)4); writer.Write((short)16); writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(samples * 4);
        for (var i = 0; i < samples; i++) { var t = i / 48000d; var on = t is >= .25 and < .5 or >= 2.8 and < 3.3 or >= 5.79 and < 5.797;
            var amplitude = t is >= 2.8 and < 3.3 ? 18000 * Math.Sin((t - 2.8) * Math.PI / .5) : 15000;
            var value = (short)(on ? Math.Sin(i * 2 * Math.PI * 997 / 48000) * amplitude : 0); writer.Write(value); writer.Write((short)-value); }
    }
    [TestMethod]
    public async Task StereoPeaksKeepShortBurstsSilenceAndTailAcrossCacheRebuild()
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-wave-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        using var audit = ComponentAudit.UseTestStore(Path.Combine(root, "audit"), false); var gate = ComponentLicenseGate.ConfirmAsync; ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
        try {
            await VideoRuntime.PrepareAsync(CancellationToken.None); var file = Path.Combine(root, "antiphase.wav"); WriteBursts(file);
            var hash = System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(file)); var a = await VideoImport.ReadAsync(file, root, CancellationToken.None);
            var wave = await VideoPreviewAssets.WaveformAsync(a, root, CancellationToken.None);
            Assert.IsTrue(wave.Count >= 1500); Assert.AreEqual(.004, wave.BinSeconds, .00001);
            Assert.AreEqual(0, wave.Peak(1, 2)); Assert.IsTrue(wave.Peak(.3, .35) > .44, "Antiphase stereo must not vanish through mono cancellation");
            Assert.IsTrue(wave.Peak(5.79, 5.797) > .44, "A seven-millisecond burst near the end must remain visible");
            Assert.AreEqual(0, wave.Peak(5.9, 6)); Assert.IsTrue(wave.Peak(0, 6) > .54);
            var cache = VideoWaveform.CachePath(a, root); var stamp = File.GetLastWriteTimeUtc(cache);
            var cached = await VideoPreviewAssets.WaveformAsync(a, root, CancellationToken.None); Assert.AreEqual(stamp, File.GetLastWriteTimeUtc(cache)); Assert.AreEqual(wave.Peak(2.8, 3.3), cached.Peak(2.8, 3.3));
            await File.WriteAllBytesAsync(cache, [1, 2, 3]); var rebuilt = await VideoPreviewAssets.WaveformAsync(a, root, CancellationToken.None); Assert.IsTrue(rebuilt.Peak(5.79, 5.797) > .44);
            a.LastWriteUtc = a.LastWriteUtc.AddTicks(1); await VideoPreviewAssets.WaveformAsync(a, root, CancellationToken.None);
            using (var reader = new BinaryReader(File.OpenRead(cache))) { reader.ReadInt32(); reader.ReadInt64(); Assert.AreEqual(a.LastWriteUtc.Ticks, reader.ReadInt64()); }
            using var cancel = new CancellationTokenSource(); cancel.Cancel(); await Assert.ThrowsAsync<OperationCanceledException>(() => VideoPreviewAssets.WaveformAsync(a, root, cancel.Token));
            CollectionAssert.AreEqual(hash, System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(file)));
            var evidence = Environment.GetEnvironmentVariable("LOPATA_VIDEO_WAVE_EVIDENCE") ?? root; Directory.CreateDirectory(evidence);
            await File.WriteAllTextAsync(Path.Combine(evidence, "waveform.json"), System.Text.Json.JsonSerializer.Serialize(new { wave.Count, wave.BinSeconds, Silence = wave.Peak(1, 2), Attack = wave.Peak(.25, .5), Tail = wave.Peak(5.79, 5.797), CacheBytes = new FileInfo(cache).Length, SourceUnchanged = true }));
        }
        finally { ComponentLicenseGate.ConfirmAsync = gate; }
    }
    [TestMethod]
    public void HierarchicalRangeMaxDoesNotSkipPeaksAtLowZoom()
    {
        var values = new float[20000]; values[19999] = .9f; values[10001] = .4f; var wave = new VideoWaveform(192, values);
        Assert.AreEqual(.9, wave.Peak(0, 80), .0001); Assert.AreEqual(0, wave.Peak(0, 10)); Assert.AreEqual(.4, wave.Peak(40, 41), .0001); Assert.AreEqual(0, wave.Peak(80, 90));
    }
}
