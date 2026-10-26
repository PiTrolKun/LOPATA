using System.IO;
using AIHub.Services.MusicScore;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoInstrumentalTests
{
    [TestMethod]
    public async Task NativeTransferPreservesTheMelodyGridAndHarmonyAndRejectsEmptyMusic()
    {
        var before = await MusicScoreNative.InspectAsync(MusicScoreTests.Abc, default);
        var after = await MusicScoreNative.InstrumentalAsync(MusicScoreTests.Abc, default);
        Assert.IsTrue(before.Sheet.Notes.Vocal.Length > 0); Assert.AreEqual(0, after.Sheet.Notes.Vocal.Length);
        Assert.IsTrue(before.Sheet.Notes.Vocal.All(after.Sheet.Notes.Ins.Contains));
        CollectionAssert.AreEqual(before.Sheet.Chords, after.Sheet.Chords); CollectionAssert.AreEqual(before.Sheet.Bars, after.Sheet.Bars);
        Assert.AreEqual(before.Sheet.Bpm, after.Sheet.Bpm);
        var repeated = await MusicScoreNative.InstrumentalAsync(after.Abc, default); Assert.AreEqual(after.Abc, repeated.Abc);
        var empty = "X:1\nT:\nM:4/4\nL:1/16\nQ:1/4=120\nV: Vocal clef=treble\nV: Ins clef=treble\nK:C\nV: Vocal\nz16|\nV: Ins\nz16|\n";
        await Assert.ThrowsAsync<InvalidDataException>(() => MusicScoreNative.InstrumentalAsync(empty, default));
    }
    [TestMethod]
    public async Task ExplicitUserPlansAreConvertedWithoutChangingTheirSourceFiles()
    {
        var paths = Environment.GetEnvironmentVariable("LOPATA_VIDEO_INSTRUMENTAL_PLANS");
        if (string.IsNullOrEmpty(paths)) { Assert.Inconclusive("Explicit existing plans required"); return; }
        var evidence = Environment.GetEnvironmentVariable("LOPATA_VIDEO_MUSIC_EVIDENCE")!; Directory.CreateDirectory(evidence); var index = 0;
        foreach (var path in paths.Split('|')) {
            var raw = await File.ReadAllTextAsync(path); var original = AIHub.Models.MusicScoreVersion.Stamp(raw);
            var before = await MusicScoreNative.InspectAsync(raw, default); Assert.IsTrue(before.Sheet.Notes.Vocal.Length > 0);
            var after = await MusicScoreNative.InstrumentalAsync(raw, default); Assert.AreEqual(0, after.Sheet.Notes.Vocal.Length);
            Assert.IsTrue(before.Sheet.Notes.Vocal.All(after.Sheet.Notes.Ins.Contains)); CollectionAssert.AreEqual(before.Sheet.Chords, after.Sheet.Chords);
            Assert.AreEqual(original, AIHub.Models.MusicScoreVersion.Stamp(await File.ReadAllTextAsync(path)));
            await File.WriteAllTextAsync(Path.Combine(evidence, $"user-plan-{++index}.instrumental.abc"), after.Abc);
        }
    }
}
