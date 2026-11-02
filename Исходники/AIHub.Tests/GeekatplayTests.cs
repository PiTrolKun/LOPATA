using System.IO;
using System.Text.Json;
using AIHub.Services.SongClip;

namespace AIHub.Tests;

[TestClass]
public sealed class GeekatplayTests
{
    [TestMethod]
    public void Fl2vaIsDistinctAndSharedSdxlEncoderVaeFilesReuseTheirOriginalInstallations()
    {
        var root=Path.GetTempPath();var cards=GeekatplayComponents.Cards(root);
        Assert.AreEqual(cards.Length,cards.Select(c=>c.ModelArtifactId).Distinct().Count());
        Assert.IsFalse(cards.SelectMany(c=>c.Files).Any(f=>f.RelativePath.Contains("ref2va")),"A new Geekatplay user must not download unused Ref2VA weights.");
        var fl=cards.Single(c=>c.ModelArtifactId==GeekatplayComponents.ModelId).Files.Single();
        Assert.Contains("fl2va_pruned_int8",fl.RelativePath);Assert.AreEqual(20_970_379_616,fl.SizeBytes);
        var shared=cards.Single(c=>c.ModelArtifactId=="song-clip-geek-shared-models");
        var full=AIHub.Services.VideoPreparation.VideoH3Catalog.Cards(root).Single(c=>c.ModelArtifactId=="video-h3-models");
        Assert.AreEqual(full.InstallDirectory,shared.InstallDirectory);Assert.HasCount(2,shared.Files);
        foreach(var file in shared.Files)Assert.AreEqual(full.Files.Single(f=>f.RelativePath==file.RelativePath).Sha256,file.Sha256);
        var sdxl=GlitchframeComponents.Cards(root).Single(c=>c.ModelArtifactId=="song-clip-glitch-sdxl");
        Assert.AreEqual(sdxl.InstallDirectory,cards.Single(c=>c.ModelArtifactId==sdxl.ModelArtifactId).InstallDirectory);
        Assert.AreEqual(cards.Sum(c=>c.TotalBytes),GeekatplayComponents.TotalBytes);
    }
    [TestMethod]
    public void SourceAndRuntimeInventoriesIdentifyTheSamePinnedFiles()
    {
        var card=GeekatplayComponents.Cards(Path.GetTempPath()).Single(c=>c.ModelArtifactId==GeekatplayComponents.SourceId);
        var tree=GeekatplayComponents.Read<AIHub.Services.PinnedTreeFile[]>("Tree");
        Assert.AreEqual(GeekatplayComponents.Revision,card.Revision);Assert.HasCount(2,tree);
        foreach(var file in card.Files){var entry=tree.Single(t=>t.Path==file.RelativePath);Assert.AreEqual(file.SizeBytes,entry.Size);Assert.AreEqual(file.Sha256,entry.Sha256);Assert.Contains(GeekatplayComponents.Revision,file.SourceUrl);}
        Assert.IsTrue(card.Files.Any(f=>f.RelativePath=="LICENSE"));
    }
    [TestMethod]
    public void NonStandardTailIsPreservedAndLyricsUseTheirRealSceneIntervals()
    {
        var attempt=Attempt() with{Timings="[00:01.00]кот\n[00:06.10]часы"};
        using var analysis=JsonDocument.Parse("""{"schema":1,"durationSeconds":10.01,"slots":[{"index":1,"startSeconds":0,"endSeconds":6,"frames":144},{"index":2,"startSeconds":6,"endSeconds":10.041666666666666,"frames":97}]}""");
        var slots=GeekatplayRunner.Slots(attempt,analysis.RootElement);
        Assert.AreEqual(10.01,slots[^1].EndSeconds);CollectionAssert.AreEqual(new[]{"кот"},slots[0].LyricAnchors);CollectionAssert.AreEqual(new[]{"часы"},slots[1].LyricAnchors);
    }
    [TestMethod]
    [DataRow(0)][DataRow(1)][DataRow(2)]
    public void InvalidOrIncompleteBeatBoundariesCannotReachThePlanner(int defect)
    {
        var rows=defect switch{
            0=>"{\"index\":1,\"startSeconds\":1,\"endSeconds\":10,\"frames\":216}",
            1=>"{\"index\":1,\"startSeconds\":0,\"endSeconds\":9,\"frames\":216}",
            _=>"{\"index\":1,\"startSeconds\":0,\"endSeconds\":10,\"frames\":241}"};
        using var document=JsonDocument.Parse("{\"schema\":1,\"durationSeconds\":10,\"slots\":["+rows+"]}");
        Assert.Throws<InvalidDataException>(()=>GeekatplayRunner.Slots(Attempt(),document.RootElement));
    }
    [TestMethod]
    [DataRow(0)][DataRow(1)][DataRow(2)]
    public void UnsupportedSettingsAreRejectedBeforeBackgroundWork(int defect)
    {
        var attempt=Attempt();var settings=attempt.Settings.Geek!;
        attempt=attempt with{Settings=attempt.Settings with{Geek=defect switch{
            0=>settings with{Width=609},1=>settings with{ClipSeconds=11},_=>settings with{ReferenceStrength=double.NaN}}}};
        Assert.Throws<InvalidDataException>(()=>GeekatplayRunner.ValidateSettings(attempt));
    }
    [TestMethod]
    public void NewSettingsDoNotChangeHistoricalSnapshotJson()
    {
        var settings=new SongClipSettings();Assert.IsFalse(JsonSerializer.Serialize(settings).Contains("\"Geek\""));
        var copy=JsonSerializer.Deserialize<SongClipSettings>(JsonSerializer.Serialize(settings));Assert.AreEqual(settings,copy);
        GeekatplayRunner.ValidateSettings(Attempt());Assert.AreEqual("executor",SongClipPlannerModel.ModelIdFor(GeekatplayRunner.ModelId));
    }
    [TestMethod]
    public async Task ComponentVerificationMetadataDoesNotInvalidateAcceptedAttemptButPinnedContentDoes()
    {
        var folder = Path.Combine(Path.GetTempPath(), "geek-binding-" + Guid.NewGuid().ToString("N"));
        try {
            var store = new SongClipStore(folder);
            var attempt = Attempt();
            var song = store.Resolve(attempt.ProjectId, attempt.Song);
            Directory.CreateDirectory(Path.GetDirectoryName(song)!);
            await File.WriteAllTextAsync(song, "unchanged song fixture");
            var job = store.Resolve(attempt.ProjectId, "attempts/" + attempt.Id);
            Directory.CreateDirectory(job);
            var file = new AIHub.Models.ManagedModelArtifactFile { RelativePath="model.bin", SizeBytes=5, Sha256="abc" };
            AIHub.Models.ManagedModelArtifactCard[] cards = [new() { ModelArtifactId="test", Revision="pinned", Files=[file] }];
            await GeekatplayBinding.EnsureAsync(attempt, store, job, cards, CancellationToken.None);
            var binding = await File.ReadAllTextAsync(Path.Combine(job, "geek-input-binding.json"));
            file.VerifiedSizeBytes = 5;
            file.VerifiedLastWriteTimeUtc = DateTimeOffset.UtcNow;
            await GeekatplayBinding.EnsureAsync(attempt, store, job, cards, CancellationToken.None);
            Assert.AreEqual(binding, await File.ReadAllTextAsync(Path.Combine(job, "geek-input-binding.json")));
            file.Sha256 = "changed";
            await Assert.ThrowsAsync<InvalidDataException>(() => GeekatplayBinding.EnsureAsync(attempt, store, job, cards, CancellationToken.None));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    private static SongClipAttempt Attempt()=>new(Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"),"attachments/song.wav","","test","кот","",
        new(){Seed=742,Geek=new()},Path.GetTempPath(),8){Model=GeekatplayRunner.ModelId,PlanningVersion=1,References=[]};
}
