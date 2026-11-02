using System.IO;
using System.Text.Json;
using AIHub.Services.SongClip;

namespace AIHub.Tests;

[TestClass]
public sealed class SongClipInfiniteTalkPlanningTests
{
    [TestMethod]
    [DataRow(.12,1)] [DataRow(3.24,1)] [DataRow(3.28,2)] [DataRow(20d,7)] [DataRow(300d,105)]
    public void NativeWindowsCoverTheSongAndKeepNineOverlappingFrames(double duration,int count)
    {
        var attempt=Attempt();var slots=SongClipVisualProtocol.Slots(attempt,duration);
        Assert.AreEqual(count,slots.Length);Assert.AreEqual(0d,slots[0].StartSeconds);
        Assert.AreEqual(duration,slots[^1].EndSeconds);
        for(var index=0;index<slots.Length;index++) {
            Assert.AreEqual(index+1,slots[index].Index);
            Assert.AreEqual(index*72/25d,slots[index].StartSeconds,1e-9);
            if(index>0)Assert.AreEqual(9/25d,slots[index-1].EndSeconds-slots[index].StartSeconds,1e-9);
        }
        // Final encoding remains 24 fps; it must not change native Wan/audio window times.
        Assert.AreEqual(24,attempt.Settings.Fps);
    }

    [TestMethod]
    public void PlanUsesTheReferencedPerformerAndBindsSettingsWithoutChangingLegacyJson()
    {
        var attempt=Attempt();var system=SongClipVisualProtocol.BuildSystem(InfiniteTalkRunner.ModelId);
        Assert.Contains("audio-driven musical PERFORMANCE",system);
        Assert.Contains("Do not substitute a lyric narrator",system);
        Assert.DoesNotContain("H3",system);
        using var input=JsonDocument.Parse(SongClipVisualProtocol.BuildInput(attempt,20,
            SongClipVisualProtocol.Slots(attempt,20).Take(6).ToArray()));
        Assert.AreEqual("single mandatory performer identity",input.RootElement.GetProperty("referenceImageRole").GetString());
        Assert.AreEqual("executor",SongClipPlannerModel.ModelIdFor(attempt.Model));
        var originalHash=SongClipVisualProtocol.InputHash(attempt,20);
        Assert.AreNotEqual(originalHash,SongClipVisualProtocol.InputHash(attempt with {
            Settings=attempt.Settings with {InfiniteTalk=attempt.Settings.InfiniteTalk! with {AudioCfg=3}}},20));
        var legacy=new SongClipSettings();using var saved=JsonDocument.Parse(JsonSerializer.Serialize(legacy));
        Assert.IsFalse(saved.RootElement.TryGetProperty("InfiniteTalk",out _));
    }

    [TestMethod]
    public void InfiniteTalkSettingsDoNotRewriteHistoricalSnapshots()
    {
        using var files=new VideoPreparationTests.TestFiles();var store=new SongClipStore(files.File("projects"));
        var legacy=Attempt() with {Model="glitchframe",Reference="",References=null,PlanningVersion=0,
            Settings=new SongClipSettings()};
        store.EnsureAttemptSnapshot(legacy);
        var path=store.Resolve(legacy.ProjectId,"attempts/"+legacy.Id+"/snapshot.json");
        var bytes=File.ReadAllBytes(path);var time=File.GetLastWriteTimeUtc(path);
        var reloaded=JsonSerializer.Deserialize<SongClipAttempt>(File.ReadAllText(path))!;
        Assert.IsNull(reloaded.Settings.InfiniteTalk);store.EnsureAttemptSnapshot(reloaded);
        CollectionAssert.AreEqual(bytes,File.ReadAllBytes(path));Assert.AreEqual(time,File.GetLastWriteTimeUtc(path));
    }

    [TestMethod]
    public void OtherWindowShapesAreNotSilentlyTreatedAsH3OrAcceptedAsWan()
    {
        var attempt=Attempt();
        Assert.Throws<InvalidDataException>(()=>SongClipVisualProtocol.Slots(attempt with {
            Settings=attempt.Settings with {InfiniteTalk=attempt.Settings.InfiniteTalk! with {FrameWindow=82}}},20));
        Assert.Throws<InvalidDataException>(()=>SongClipVisualProtocol.Slots(attempt with {
            Settings=attempt.Settings with {InfiniteTalk=attempt.Settings.InfiniteTalk! with {MotionFrames=10}}},20));
    }

    [TestMethod]
    public void NativeAnalysisTimelineFlowsThroughPlannerInputAndReplyValidation()
    {
        var attempt=Attempt();var timeline=SongClipVisualProtocol.Slots(attempt,6);
        using var input=JsonDocument.Parse(SongClipVisualProtocol.BuildInput(attempt,6,timeline,timeline:timeline));
        Assert.AreEqual(2,input.RootElement.GetProperty("slots").GetArrayLength());
        var plan=SongClipVisualProtocol.ParseAndValidate(Reply(timeline),attempt,6,
            [new("executor-test","Qwen3.6-27B","fixture","song-to-clip semantic planning")],timeline,timeline:timeline);
        Assert.AreEqual(2,plan.Scenes.Length);Assert.AreEqual(3.24,plan.Scenes[0].EndSeconds);
        Assert.AreEqual(2.88,plan.Scenes[1].StartSeconds);Assert.AreEqual(6d,plan.Scenes[1].EndSeconds);
        foreach(var (scene,index) in plan.Scenes.Select((scene,index)=>(scene,index)))
            CollectionAssert.AreEqual(timeline[index].LyricAnchors,scene.LyricAnchors);
    }

    [TestMethod]
    [DataRow("start")][DataRow("end")][DataRow("index")][DataRow("anchor")][DataRow("order")][DataRow("missing")]
    public void SuppliedInfiniteTalkTimelineCannotReplaceAnyNativeBoundaryOrAnchor(string defect)
    {
        var attempt=Attempt();var native=SongClipVisualProtocol.Slots(attempt,6);var timeline=native.ToArray();
        switch(defect) {
            case "start":timeline[1]=timeline[1] with{StartSeconds=2.9};break;
            case "end":timeline[1]=timeline[1] with{EndSeconds=5.9};break;
            case "index":timeline[1]=timeline[1] with{Index=1};break;
            case "anchor":timeline[0]=timeline[0] with{LyricAnchors=["Unrelated invented source line."]};break;
            case "order":Array.Reverse(timeline);break;
            case "missing":timeline=timeline[..1];break;
        }
        Assert.Throws<InvalidDataException>(()=>SongClipVisualProtocol.BuildInput(attempt,6,native,timeline:timeline));
        Assert.Throws<InvalidDataException>(()=>SongClipVisualProtocol.ParseAndValidate(Reply(native),attempt,6,
            [new("executor-test","Qwen3.6-27B","fixture","song-to-clip semantic planning")],native,timeline:timeline));
    }

    [TestMethod]
    public void OtherStaticPipelinesStillRejectForeignTimelines()
    {
        var infinite=Attempt();var timeline=SongClipVisualProtocol.Slots(infinite,6);
        foreach(var model in new[]{"h3_music_context","glitchframe"})
            Assert.Throws<InvalidDataException>(()=>SongClipVisualProtocol.BuildInput(infinite with{Model=model},6,timeline:timeline));
    }

    [TestMethod]
    [DataRow("ru")] [DataRow("en")]
    public void RejectedPerformerMechanicIsNotAdvertisedByCloudNavigation(string language)
    {
        var scenario=AIHub.Services.ScenarioNavigationCatalog.Get(AIHub.Services.ScenarioNavigationCatalog.SongClip);
        Assert.IsFalse(AIHub.Services.ScenarioNavigationCatalog.CloudTags.Any(tag=>tag.Id=="song_clip_performer"));
        Assert.DoesNotContain("song_clip_performer",scenario.Tags);
        var localization=new AIHub.Services.LocalizationService();localization.Load(language);
        Assert.DoesNotContain("InfiniteTalk",localization.T("Cloud.Description.song_clip"));
        Assert.AreNotEqual("SongClip.ModelRetired",localization.T("SongClip.ModelRetired"));
    }

    private static string Reply(SongClipVisualScene[] slots)=>JsonSerializer.Serialize(new{
        globalStyle="Natural cinematic light and restrained camera movement preserve a coherent continuous performance.",
        scenes=slots.Select(s=>new{index=s.Index,startSeconds=s.StartSeconds,endSeconds=s.EndSeconds,
            prompt="The referenced performer sings calmly in one coherent camera view with subtle natural gestures during passage "+s.Index+".",
            lyricAnchors=s.LyricAnchors})
    });

    private static SongClipAttempt Attempt()=>new(Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"),
        "attachments/song.wav","attachments/singer.png","Song","The cat watches the moon.","[00:03.00]The cat watches the moon.",
        new(){Seed=12,InfiniteTalk=new()},Path.Combine(Path.GetTempPath(),"model-store"),4) {
        Model=InfiniteTalkRunner.ModelId,PlanningVersion=1,
        References=[new("attachments/singer.png","performer","The singer performs the song.")]
    };
}
