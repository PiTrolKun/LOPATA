using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.MusicMidi;
using AIHub.Services.MusicScore;

namespace AIHub.Tests;

[TestClass,DoNotParallelize]
public sealed class MusicScoreTests
{
    internal const string Abc="""
        X:1
        T:
        M:4/4
        L:1/32
        Q:1/4=120
        V: Vocal clef=treble name="Vocal Melody" snm="Vocal"
        V: Ins clef=treble name="Ins Melody" snm="Inst."
        K:C
        % verse
        V: Vocal
        "C"C8 D8 E8 F8|
        V: Ins
        Z|
        % chorus
        V: Vocal
        "G"G8 A8 B8 c8|
        V: Ins
        Z|
        """;
    private static MusicScoreVersion Version(string abc=Abc,string lyrics="[Verse]\nHello\n[Chorus]\nWorld")=>new() {
        Abc=abc,Hash=MusicScoreVersion.Stamp(abc),InputHash=MusicScoreVersion.Bind("pop",lyrics,"full",[]),Name="score"};
    private static MusicScoreWorkspace Workspace(params MusicScoreVersion[] versions)=>new() {Enabled=true,Versions=versions,Selected=versions.Select(v=>v.Id).ToArray()};
    [TestMethod]
    public async Task NativeReaderAndMirrorPreserveNotesAndRejectIncompatibleEdits() {
        var before=await MusicScoreNative.InspectAsync(Abc,default);
        Assert.AreEqual(120,before.Sheet.Bpm);Assert.AreEqual(4d,before.Sheet.Seconds);Assert.HasCount(2,before.Sheet.Sections);
        var result=await MusicScoreNative.MirrorAsync(Abc,0,[1],default);
        CollectionAssert.AreEqual(before.Sheet.Notes.Vocal,result.Sheet.Notes.Vocal);
        CollectionAssert.AreEqual(before.Sheet.Notes.Ins,result.Sheet.Notes.Ins);
        Assert.AreEqual("C",result.Sheet.Chords[1].Name);
        Assert.AreEqual("G",before.Sheet.Chords[1].Name);
        await Assert.ThrowsAsync<InvalidDataException>(()=>MusicScoreNative.MirrorAsync(Abc,0,[0],default));
        await Assert.ThrowsAsync<InvalidDataException>(()=>MusicScoreNative.InspectAsync(Abc.Replace("C8 D8","C5 D8"),default));
    }
    [TestMethod]
    public async Task MarkIsCompatibleWithUpstreamAndUnicodeInputsArePreserved() {
        var marked=await MusicScoreNative.MarkAsync(Abc," warm pop\n","[Verse]\nПривет","full",false,default);
        StringAssert.Contains(marked,"%yue2-words ");
        var kept=await MusicScoreNative.MarkAsync(marked,"warm pop","[Verse]\nПривет\n","full",true,default);
        Assert.EndsWith(" keep",kept);
        var sheet=await MusicScoreNative.InspectAsync(kept,default);Assert.HasCount(8,sheet.Sheet.Notes.Vocal);
        Assert.AreEqual(marked[(marked.LastIndexOf("%yue2-words ",StringComparison.Ordinal)+12)..],
            kept[(kept.LastIndexOf("%yue2-words ",StringComparison.Ordinal)+12)..^5]);
        Assert.IsTrue(await MusicScoreNative.CanScheduleAsync(Abc,"[Verse]\nПривет\n[Chorus]\nМир",default));
        Assert.IsFalse(await MusicScoreNative.CanScheduleAsync(Abc,"no section labels",default));
    }
    [TestMethod]
    public async Task SelectedPlansBypassPlannerAndCheckpointDoesNotRepeatCompletedAudio() {
        using var files=new MusicProjectTests.Files();var jobs=new MusicGenerationJobs(Path.Combine(files.Root,"jobs"));
        var expert=new MusicExpertSettings {Variation=MusicStudioRuntime.Variation};
        var a=Version();var b=Version(Abc.Replace("Q:1/4=120","Q:1/4=100"));
        var job=jobs.Create(files.Root,Path.Combine(files.Root,"audio"),"Plans",2,8,"pop","[Verse]\nHello\n[Chorus]\nWorld",expert:expert);
        job=await MusicScorePipeline.CaptureAsync(jobs,job,Workspace(a,b),default);
        var worker=new Worker();await new MusicGenerationRunner(jobs,worker).RunAsync(job.Id,default);
        await new MusicGenerationRunner(jobs,worker).RunAsync(job.Id,default);
        Assert.AreEqual(0,worker.Plans);Assert.AreEqual(2,worker.Synths);
        var completed=jobs.Load(job.Id);
        Assert.AreEqual(a.Id,completed.Variants[0].ScoreVersionId);Assert.AreEqual(b.Id,completed.Variants[1].ScoreVersionId);
        Assert.IsNotNull(completed.Variants[0].EffectivePlanHash);
        StringAssert.Contains(File.ReadAllText(completed.Variants[1].EffectivePlanFile!),"Q:1/4=100");
        var metadata=MusicSongMetadata.Create(completed,completed.Variants[0],0)["LOPATA_SCORE"];
        using var provenance=JsonDocument.Parse(metadata);
        Assert.AreEqual(a.Id,provenance.RootElement.GetProperty("ScoreVersionId").GetString());
        Assert.AreEqual(completed.Variants[0].EffectivePlanHash,provenance.RootElement.GetProperty("effectiveHash").GetString());
        Assert.IsFalse(metadata.Contains(files.Root,StringComparison.OrdinalIgnoreCase));
    }
    [TestMethod]
    public async Task ChangedInputRequiresExplicitKeepAndMusicalSourcesCannotBeMixed() {
        using var files=new MusicProjectTests.Files();var jobs=new MusicGenerationJobs(Path.Combine(files.Root,"jobs"));
        var expert=new MusicExpertSettings{Variation=MusicStudioRuntime.Variation};var state=Workspace(Version());
        var job=jobs.Create(files.Root,Path.Combine(files.Root,"audio"),"Changed",1,8,"pop","new words",expert:expert);
        await Assert.ThrowsAsync<InvalidDataException>(()=>MusicScorePipeline.CaptureAsync(jobs,job,state,default));
        var kept=await MusicScorePipeline.CaptureAsync(jobs,job,state with {Keep=true},default);
        Assert.IsNotNull(kept.Variants[0].PlanHash);
        Assert.Throws<InvalidDataException>(()=>MusicScorePipeline.Validate(state,MusicStudioRuntime.Variation,expert,new(){Enabled=true},null,"lyrics"));
        Assert.Throws<InvalidDataException>(()=>MusicScorePipeline.Validate(state,MusicAceCatalog.Variation,expert,null,null,"lyrics"));
    }
    [TestMethod]
    public void SchedulingIsExplicitAndOnlySupportedRequestFieldsAreSent() {
        var expert=new MusicExpertSettings{Variation=MusicStudioRuntime.Variation};expert.Values["cfg_scale"]=1.11;
        Assert.Throws<InvalidDataException>(()=>MusicScorePipeline.Validate(new(){LyricTiming=true},MusicStudioRuntime.Variation,expert,null,null,"words"));
        expert.Values["cfg_scale"]=1;
        var request=new MusicYueRequest("pop","[Verse]\nHello",1,2,8){Expert=expert,Abc=Abc,ScoreLyricTiming=true,FollowScoreSections=true};
        using var body=JsonDocument.Parse(JsonSerializer.Serialize(MusicStudioWorker.Request(request,false,MusicStudioRuntime.Current)));
        Assert.IsTrue(body.RootElement.GetProperty("lyric_timing").GetBoolean());
        Assert.IsTrue(body.RootElement.GetProperty("harmony").GetProperty("follow_lyrics").GetBoolean());
        using var original=JsonDocument.Parse(JsonSerializer.Serialize(MusicStudioWorker.Request(request with {ScoreLyricTiming=false,FollowScoreSections=false},false,MusicStudioRuntime.Current)));
        Assert.IsFalse(original.RootElement.GetProperty("lyric_timing").GetBoolean());Assert.IsFalse(original.RootElement.TryGetProperty("harmony",out _));
    }
    [TestMethod]
    public void APlanOnlyProjectIsDurableWithoutAFakeAudioHistoryStep() {
        using var files=new MusicProjectTests.Files();var store=new MusicProjects(Path.Combine(files.Root,"projects"));
        var draft=store.CreateDraft();var snapshot=new MusicProjectSnapshot{Scores=Workspace(Version())};
        Assert.IsFalse(store.SaveWorkspace(draft,snapshot).Persistent);
        var saved=store.SaveWorkspace(draft,snapshot,persistDraft:true);
        var loaded=store.Load(saved.Id);Assert.IsFalse(loaded.Manual);Assert.HasCount(0,loaded.Steps);
        Assert.AreEqual(snapshot.Scores.Selected[0],loaded.Saved.Scores.Selected[0]);
        Assert.Throws<InvalidDataException>(()=>(snapshot.Scores with {Versions=[snapshot.Scores.Versions[0] with {Abc="changed"}]}).Validate());
        var old=JsonSerializer.Deserialize<MusicProjectSnapshot>("{}")!;Assert.IsFalse(old.Scores.Enabled);Assert.HasCount(0,old.Scores.Versions);
    }
    [TestMethod]
    public async Task PreviewIsRealPcmAndContainsBothVoices() {
        using var files=new MusicProjectTests.Files();Directory.CreateDirectory(files.Root);
        var sheet=(await MusicScoreNative.InspectAsync(Abc,default)).Sheet;
        var path=Path.Combine(files.Root,"preview.wav");MusicScorePreview.Write(path,sheet,default);
        Assert.AreEqual(4d,MusicWaveFile.ReadDuration(path).TotalSeconds);
        Assert.IsTrue(File.ReadAllBytes(path).Skip(44).Any(b=>b!=0));
    }
    [TestMethod]
    public Task NewProjectClearsPlansAndUnsupportedButtonsExplainTheModel()=>ScenarioNavigationTests.Sta(()=> {
        using var files=new MusicProjectTests.Files();
        using var view=new AIHub.Controls.MusicWorkspaceControl(new MusicProjects(Path.Combine(files.Root,"projects")),new MusicGenerationJobs(Path.Combine(files.Root,"jobs")),new MusicOutputPreferences(Path.Combine(files.Root,"output.json")));
        view.Generation.ConfigureVariation(MusicStudioRuntime.Variation,new MusicExpertSettings{Variation=MusicStudioRuntime.Variation});
        view.Localize(k=>k);view.Projects.RememberDefaults();view.Generation.Scores=Workspace(Version());view.Projects.New();
        Assert.HasCount(0,view.Generation.Scores.Versions);Assert.IsFalse(view.Generation.Scores.Enabled);
        view.Generation.ConfigureVariation(MusicDiffRhythmCatalog.Variation,MusicModelVariants.Defaults(MusicDiffRhythmCatalog.Variation));
        Assert.IsFalse(view.Generation.ScorePlansButton.IsEnabled);Assert.IsTrue(System.Windows.Controls.ToolTipService.GetShowOnDisabled(view.Generation.ScorePlansButton));
        StringAssert.Contains(view.Generation.ScorePlansButton.ToolTip!.ToString()!,"Music.Score.Unsupported");
    });
    [TestMethod]
    public async Task PausedQueueRestoresOnlyTheUnfinishedPlanAfterRestart() {
        using var files=new MusicProjectTests.Files();using var lifetime=new CancellationTokenSource();
        var plans=new MusicScorePlanStore(Path.Combine(files.Root,"plans"));
        var backgroundStore=new BackgroundOperationStore(Path.Combine(files.Root,"background.json"));
        var previous=ApplicationBackgroundOperations.Current;var previousExit=ApplicationBackgroundOperations.ExitToken;
        void Host(BackgroundOperationController? c,CancellationToken t=default){
            typeof(ApplicationBackgroundOperations).GetProperty(nameof(ApplicationBackgroundOperations.Current))!.SetValue(null,c);
            typeof(ApplicationBackgroundOperations).GetProperty(nameof(ApplicationBackgroundOperations.ExitToken))!.SetValue(null,t);
        }
        try {
            var input=new MusicProjectSnapshot {Variation=MusicStudioRuntime.Variation,Expert=new(){Variation=MusicStudioRuntime.Variation},Lyrics="[Verse]\nHello"};
            var work=new MusicScorePlanRequest(Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"),files.Root,input,"pop",[],
                MusicStudioRuntime.Current.Revision,MusicStudioRuntime.Current.Pack,[new(1),new(2)]);
            var controller=new BackgroundOperationController(backgroundStore);Host(controller,lifetime.Token);
            var first=new Planner{DelaySecond=true};var run=MusicScorePlanWork.RunAsync(work,first,_=>{},lifetime.Token,storage:plans);
            await first.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await controller.PauseAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsNotNull(plans.Load(work.Id).Candidates[0].Version);Assert.IsNull(plans.Load(work.Id).Candidates[1].Version);
            controller.CheckpointForExit();lifetime.Cancel();try{await run.WaitAsync(TimeSpan.FromSeconds(10));}catch(OperationCanceledException){}
            var restored=new BackgroundOperationController(backgroundStore);restored.Load();Host(restored);
            var second=new Planner();
            await MusicScorePlanWork.RunAsync(plans.Load(work.Id),second,_=>{},default,restored.State,storage:plans);
            Assert.AreEqual(1,second.Plans);Assert.IsTrue(plans.Load(work.Id).Candidates.All(c=>c.Version is not null));
            Assert.AreEqual(BackgroundOperationPhase.Completed,restored.State!.Phase);
        }finally{Host(previous,previousExit);}
    }
    private sealed class Planner:IMusicYueWorker {
        public int Plans;public bool DelaySecond;public TaskCompletionSource SecondStarted=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event Action<string>? Log{add{}remove{}}
        public async Task PlanAsync(string model,MusicYueRequest request,string requestPath,string planPath,CancellationToken token) {
            Plans++;if(DelaySecond&&Plans==2){SecondStarted.TrySetResult();await Task.Delay(Timeout.Infinite,token);}
            await File.WriteAllTextAsync(planPath,Abc,token);
        }
        public Task SynthesizeAsync(string model,string decoder,MusicYueRequest request,string requestPath,string outputPath,CancellationToken token)=>throw new AssertFailedException("Planning must not synthesize a song");
    }
    private sealed class Worker:IMusicYueWorker {
        public int Plans,Synths;public string? LastRequestReceipt{get;private set;}
        public event Action<string>? Log{add{}remove{}}
        public Task PlanAsync(string model,MusicYueRequest request,string requestPath,string planPath,CancellationToken token){Plans++;throw new AssertFailedException("Saved score must bypass planner");}
        public Task SynthesizeAsync(string model,string decoder,MusicYueRequest request,string requestPath,string outputPath,CancellationToken token){
            Synths++;MusicOutputTests.WriteWave(outputPath);LastRequestReceipt=JsonSerializer.Serialize(new{abc=request.Abc});return Task.CompletedTask;
        }
    }
}
