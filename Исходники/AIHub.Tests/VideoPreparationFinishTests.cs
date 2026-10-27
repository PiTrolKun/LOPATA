using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Services.VideoPreparation;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoPreparationFinishTests
{
    internal static VideoPreparationProject ReadyProject()
    {
        var p = VideoWorldTests.Root(); p.Title = "Кот <script>не код</script>";
        var c = p.World!.Cards[0]; c.Name = "Workshop"; c.Text.IdeaText = "Warm workshop, a cat with a clock.";
        VideoWorld.Capture(p); VideoTranslationHistory.UseOriginal(p.World.Working);
        VideoReferences.Ensure(p).Choice = "skip";
        var d = VideoShots.Ensure(p); d.Attempted = true;
        var scene = new VideoScenePlan { Name = "Workshop", Intent = "The cat makes a clock", Shots = [
            new() { Name = "=opening", Start = "The cat turns a key", End = "The clock starts", Duration = 2 },
            new() { Name = "Finale", Start = "The filmmaker waves", End = "LOPATA says «Снято!»", Duration = 3 } ] };
        d.Scenes.Add(scene); d.Source = VideoShots.Hash(VideoShots.Source(p)); VideoShots.Capture(p);
        var translation = new VideoShotTranslation { Id = Guid.NewGuid().ToString("N"), NativeSnapshot = VideoShots.Snapshot([scene]), Scene = VideoShots.Copy(scene) };
        d.Translations.Add(translation); d.SelectedTranslations[scene.Id] = translation.Id;
        p.CurrentStep = "storyboard"; VideoStoryboard.Ensure(p); p.Validate();
        Assert.IsTrue(VideoPreparationReadiness.Ready(p), string.Join(",", VideoPreparationReadiness.Check(p).Where(x => x.Blocking).Select(x => x.Key)));
        return p;
    }
    [TestMethod]
    public void BoardEditingHistorySeekAndStalePlansSurviveArchive()
    {
        using var f = new VideoPreparationTests.TestFiles(); var p = ReadyProject(); var b = p.Storyboard!;
        var original = b.Frames[0].Id; Assert.AreEqual(5, VideoStoryboard.Duration(p));
        Assert.AreEqual(b.Frames[1].Id, VideoStoryboard.At(p, 2).Frame!.Id);
        b.Frames.Reverse(); b.Frames[0].Seconds = 7; VideoStoryboard.Capture(p);
        Assert.IsTrue(VideoStoryboard.MoveVersion(p, -1)); Assert.AreEqual(original, b.Frames[0].Id);
        Assert.IsTrue(VideoStoryboard.MoveVersion(p, 1)); Assert.AreEqual(7, b.Frames[0].Seconds);
        VideoGeneratorPlan.SaveChoice(p, new("ltx-2.5", 1280, 720, 24, -1, "manual notes"));
        VideoPreparationArchive.Save(p, f.File("project.lopataAIVid")); var loaded = VideoPreparationArchive.Load(f.File("project.lopataAIVid"), f.File("opened"));
        Assert.IsTrue(VideoGeneratorPlan.CanResume(loaded)); Assert.AreEqual(7, loaded.Storyboard!.Frames[0].Seconds);
        loaded.Shots!.Scenes[0].Shots[0].Start += " changed";
        Assert.IsFalse(VideoStoryboard.Current(loaded)); Assert.IsFalse(VideoPreparationReadiness.Ready(loaded));
        VideoStoryboard.Synchronize(loaded); Assert.AreEqual(7, loaded.Storyboard.Frames[0].Seconds);
        loaded.Shots.Scenes[0].Shots.RemoveAt(0); VideoStoryboard.Synchronize(loaded);
        Assert.IsFalse(VideoPreparationReadiness.BoardReady(loaded)); loaded.Validate();
    }
    [TestMethod]
    public void ResumeRequiresSavedHistoryNotJustDraftAndNeverClaimsGeneration()
    {
        var p = ReadyProject(); p.Generator = new() { FinalReached = true, Draft = new("minimax-h3", 1280, 720, 24, 12, "") };
        Assert.IsFalse(VideoGeneratorPlan.CanResume(p));
        VideoGeneratorPlan.SaveChoice(p, p.Generator.Draft); VideoTranslationHistory.UpdateProvenance(p);
        Assert.IsTrue(VideoGeneratorPlan.CanResume(p)); Assert.IsFalse(p.Provenance.Models.Any(m => m.Id == "minimax-h3"));
        p.Generator.SelectedId = ""; Assert.IsFalse(VideoGeneratorPlan.CanResume(p)); p.Validate();
        Assert.ThrowsExactly<InvalidDataException>(() => VideoGeneratorPlan.ValidateSettings(new("ltx-2.5", 1279, 720, 24, -1, "")));
    }
    [TestMethod]
    public async Task PortableExportContainsVerifiedFilesTimingsProvenanceAndNoHostPaths()
    {
        using var f = new VideoPreparationTests.TestFiles(); var p = ReadyProject();
        var source = f.File("original.wav"); var bytes = Enumerable.Range(0, 1500).Select(i => (byte)i).ToArray(); File.WriteAllBytes(source, bytes);
        var role = "video-board/audio/" + Guid.NewGuid().ToString("N"); p.Attachments.Add(new(source, role)); p.Storyboard!.Audio = new() { Role = role, Name = "original.wav", Seconds = 5 };
        var target = f.File("export"); var id = Guid.NewGuid().ToString("N");
        var result = await VideoPreparationExport.ExportAsync(p, target, default, operationId: id);
        Assert.IsGreaterThan(10, result.Files);
        using var timeline = JsonDocument.Parse(File.ReadAllText(Path.Combine(target, "timeline.json")));
        var frames = timeline.RootElement.GetProperty("data").GetProperty("frames");
        Assert.AreEqual(2, frames[0].GetProperty("end").GetDouble()); Assert.AreEqual(2, frames[1].GetProperty("start").GetDouble());
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(target, "manifest.json")));
        foreach (var entry in manifest.RootElement.GetProperty("data").GetProperty("files").EnumerateArray())
        {
            var path = Path.Combine(target, entry.GetProperty("path").GetString()!); var content = File.ReadAllBytes(path);
            Assert.AreEqual(entry.GetProperty("bytes").GetInt64(), content.LongLength);
            Assert.AreEqual(entry.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(content)));
            if (Path.GetExtension(path) is ".txt" or ".csv" or ".html" or ".md" or ".json")
            { var text = File.ReadAllText(path); StringAssert.Contains(text, "LOPATA"); Assert.IsFalse(text.Contains(f.File(""), StringComparison.OrdinalIgnoreCase)); }
        }
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(Directory.GetFiles(Path.Combine(target, "audio")).Single()));
        var csv = File.ReadAllText(Path.Combine(target, "timeline.csv")); StringAssert.Contains(csv, "'=opening");
        var html = File.ReadAllText(Path.Combine(target, "storyboard.html")); Assert.IsFalse(html.Contains("<script>не код</script>"));
        var reopened = VideoPreparationArchive.Load(Path.Combine(target, "project.lopataAIVid"), f.File("portable"));
        Assert.IsTrue(VideoPreparationReadiness.Ready(reopened));
        var again = await VideoPreparationExport.ExportAsync(p, target, default, operationId: id); Assert.AreEqual(result.Files, again.Files);
        await Assert.ThrowsExactlyAsync<IOException>(() => VideoPreparationExport.ExportAsync(p, target, default));
        File.AppendAllText(Path.Combine(target, "timeline.csv"), "tampered");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => VideoPreparationExport.ExportAsync(p, target, default, operationId: id));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(source));
    }
    [TestMethod]
    public async Task CancelAndIncompletePackageCannotPublishOrOverwriteDirectory()
    {
        using var f = new VideoPreparationTests.TestFiles(); var p = ReadyProject(); var target = f.File("cancel");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => VideoPreparationExport.ExportAsync(p, target, canceled.Token));
        Assert.IsFalse(Directory.Exists(target)); Assert.IsEmpty(Directory.GetDirectories(f.File(""), ".lopata-export-*"));
        p.Storyboard!.Frames.Clear();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => VideoPreparationExport.ExportAsync(p, target, default));
        Assert.IsFalse(Directory.Exists(target));
    }
}
