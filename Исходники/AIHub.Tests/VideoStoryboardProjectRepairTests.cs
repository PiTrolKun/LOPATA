using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.VideoPreparation;

namespace AIHub.Tests;

/// <summary>Explicit editorial migration stand. Inputs are supplied separately, never a hard-coded user's project.</summary>
[TestClass, DoNotParallelize]
public sealed class VideoStoryboardProjectRepairTests
{
    [TestMethod]
    public void ExplicitEditorialRepairPreservesOriginalHistoryFilesAndRoundTripsReady()
    {
        var source = Environment.GetEnvironmentVariable("LOPATA_STORYBOARD_REPAIR_SOURCE");
        var target = Environment.GetEnvironmentVariable("LOPATA_STORYBOARD_REPAIR_DESTINATION");
        var patch = Environment.GetEnvironmentVariable("LOPATA_STORYBOARD_REPAIR_MATERIALS");
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target) || string.IsNullOrEmpty(patch)) Assert.Inconclusive("Explicit repair paths required.");
        using var files = new VideoPreparationTests.TestFiles();
        using var data = JsonDocument.Parse(File.ReadAllText(patch)); var edits = data.RootElement;
        var p = VideoPreparationArchive.Load(source, files.File("opened"));
        Assert.AreEqual(edits.GetProperty("projectId").GetString(), p.Id);
        var originalWorld = p.World!.History.ToArray(); var oldShots = p.Shots!.History.ToArray(); var oldBoard = p.Storyboard!.History.ToArray();
        var attachments = p.Attachments.Select(a => (a.Role, Hash: Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(a.Path))))).ToArray();
        var translated = p.Shots.Scenes.ToDictionary(s => s.Id, s => VideoShots.Copy(VideoShots.SelectedTranslation(p.Shots, s.Id)!.Scene));
        var worldEnglish = p.World.Working.EnglishText;
        VideoShots.Capture(p); VideoStoryboard.Capture(p);
        p.IdeaText = edits.GetProperty("concept").GetProperty("native").GetString()!;
        ManualEnglish(p, edits.GetProperty("concept").GetProperty("english").GetString()!);
        p.Development!.IdeaText = edits.GetProperty("development").GetProperty("native").GetString()!;
        VideoDevelopment.RefreshSource(p);
        ManualEnglish(p.Development, edits.GetProperty("development").GetProperty("english").GetString()!);
        VideoWorld.Review(p); ManualEnglish(p.World.Working, worldEnglish);
        foreach (var id in edits.GetProperty("removeShots").EnumerateArray().Select(e => e.GetString()!))
        {
            Assert.AreEqual(1, p.Shots.Scenes.Sum(s => s.Shots.Count(t => t.Id == id)));
            foreach (var scene in p.Shots.Scenes) scene.Shots.RemoveAll(t => t.Id == id);
            foreach (var scene in translated.Values) scene.Shots.RemoveAll(t => t.Id == id);
        }
        foreach (var e in edits.GetProperty("scenes").EnumerateArray())
        {
            var scene = p.Shots.Scenes.Single(s => s.Id == e.GetProperty("id").GetString());
            Patch(scene, e.GetProperty("native")); Patch(translated[scene.Id], e.GetProperty("english"));
        }
        foreach (var e in edits.GetProperty("shots").EnumerateArray())
        {
            var shot = p.Shots.Scenes.SelectMany(s => s.Shots).Single(s => s.Id == e.GetProperty("id").GetString());
            Patch(shot, e.GetProperty("native"));
            Patch(translated.Values.SelectMany(s => s.Shots).Single(s => s.Id == shot.Id), e.GetProperty("english"));
        }
        var appearance = p.World.Cards.Where(c => c.Kind == "character").Select(c => c.Id).ToArray();
        foreach (var scene in p.Shots.Scenes)
        {
            foreach (var shot in scene.Shots)
            {
                if (shot.Id != edits.GetProperty("establishingShotId").GetString())
                { shot.CardIds = shot.CardIds.Concat(appearance).Distinct().ToArray(); shot.ReferenceIds = scene.ReferenceIds.ToArray(); }
                var en = translated[scene.Id].Shots.Single(t => t.Id == shot.Id);
                en.CardIds = shot.CardIds.ToArray(); en.ReferenceIds = shot.ReferenceIds.ToArray();
            }
            var enScene = translated[scene.Id];
            enScene.CardIds = scene.CardIds.ToArray(); enScene.ReferenceIds = scene.ReferenceIds.ToArray();
            var revision = new VideoShotTranslation { Id = Guid.NewGuid().ToString("N"), Manual = true,
                NativeSnapshot = VideoShots.Snapshot([scene]), Scene = enScene,
                Model = new("unknown", "Manual editorial correction", "unknown", "manual scene translation") };
            p.Shots.Translations.Add(revision); p.Shots.SelectedTranslations[scene.Id] = revision.Id;
        }
        p.Shots.Source = VideoShots.Hash(VideoShots.Source(p)); VideoShots.Capture(p);
        VideoStoryboard.Synchronize(p, restorePlanOrder: true);
        foreach (var frame in p.Storyboard.Frames) frame.ImageId = "";
        p.Storyboard.SelectedId = p.Storyboard.Frames.First().Id; VideoStoryboard.Capture(p);
        p.CurrentStep = "storyboard"; p.ModifiedUtc = DateTimeOffset.UtcNow;
        p.Validate(); Assert.IsTrue(VideoPreparationReadiness.Ready(p), string.Join(";", VideoPreparationReadiness.Check(p).Where(i => i.Blocking).Select(i => i.Key + ":" + i.Detail)));
        VideoPreparationArchive.Save(p, target); var loaded = VideoPreparationArchive.Load(target, files.File("reopened"));
        Assert.IsTrue(VideoPreparationReadiness.Ready(loaded));
        CollectionAssert.AreEqual(originalWorld, loaded.World!.History.Take(originalWorld.Length).ToArray());
        Assert.AreEqual(JsonSerializer.Serialize(oldShots), JsonSerializer.Serialize(loaded.Shots!.History.Take(oldShots.Length).ToArray()));
        CollectionAssert.AreEqual(oldBoard, loaded.Storyboard!.History.Take(oldBoard.Length).ToArray());
        CollectionAssert.AreEqual(attachments, loaded.Attachments.Select(a => (a.Role, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(a.Path))))).ToArray());
        Assert.IsTrue(VideoStoryboard.MoveVersion(loaded, -1)); Assert.IsTrue(VideoStoryboard.MoveVersion(loaded, 1));
        loaded.Validate();
        Console.WriteLine($"Repaired {p.Id}: {p.Shots.Scenes.Count} scenes, {p.Storyboard.Frames.Count} frames, {VideoStoryboard.Duration(p)}s, {p.Attachments.Count} files; old histories retained {originalWorld.Length}/{oldShots.Length}/{oldBoard.Length}.");
    }
    private static void ManualEnglish(VideoPreparationText p, string text)
    {
        VideoConceptHistory.Capture(p);
        var revision = new VideoTranslationRevision(Guid.NewGuid().ToString("N"), text, "user",
            p.TranslationRevisionId.Length == 0 ? null : p.TranslationRevisionId, p.IdeaRevisionId, p.IdeaText, p.Form, DateTimeOffset.UtcNow,
            Clarifications: "Editorial correction; no new local model generation.");
        p.TranslationHistory.Add(revision); p.TranslationRevisionId = revision.Id; p.EnglishText = text;
    }
    private static void Patch(object target, JsonElement values)
    {
        foreach (var field in values.EnumerateObject())
        {
            var property = target.GetType().GetProperty(field.Name);
            Assert.IsNotNull(property); Assert.AreEqual(typeof(string), property.PropertyType); Assert.AreNotEqual("Id", field.Name);
            property.SetValue(target, field.Value.GetString());
        }
    }
}
