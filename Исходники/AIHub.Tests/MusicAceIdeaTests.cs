using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.MusicAceIdeas;

namespace AIHub.Tests;

[TestClass,DoNotParallelize]
public sealed class MusicAceIdeaTests
{
    internal static MusicAceIdea Idea() {
        var expert=MusicAceCatalog.Defaults();var input=new MusicProjectSnapshot {Model=MusicAceCatalog.ModelName,Variation=MusicAceCatalog.Variation,
            ModelRevision=MusicAceCatalog.ModelRevision,Expert=expert,Lyrics="[Verse]\nHello",DurationSeconds=30};
        var data=new MusicAceIdeaData(string.Concat(Enumerable.Repeat("<|audio_code_123|>",150)),"pop",input.Lyrics,120,"C major","4","en",30);
        return new(){Name="1",Seed=42,BaseSoundSeed=441,Data=data,Hash=MusicAceIdea.Stamp(data.Codes),Input=input,Style="pop",
            InputHash=MusicAceIdea.Bind("pop",input.Lyrics,expert,[],30)};
    }
    internal static MusicAceIdeaWorkspace Workspace(MusicAceIdea idea)=>new(){Ideas=[idea],Selected=[idea.Id],Enabled=true};
    [TestMethod]
    public void SavedCodesDisableReplanningAndRetakeUsesASeparateSeed() {
        var idea=Idea();var use=new MusicAceIdeaUse(idea,555,.25);
        var request=new MusicYueRequest("pop",idea.Input.Lyrics,42,441,30){Expert=idea.Input.Expert,AceIdea=use};
        using var body=JsonDocument.Parse(JsonSerializer.Serialize(MusicAceRequest.Build(request)));
        var parameters=body.RootElement.GetProperty("params");
        Assert.AreEqual(idea.Data.Codes,parameters.GetProperty("audio_codes").GetString());
        Assert.IsFalse(parameters.GetProperty("thinking").GetBoolean());Assert.IsFalse(parameters.GetProperty("use_cot_caption").GetBoolean());
        Assert.AreEqual(441,parameters.GetProperty("seed").GetInt32());Assert.AreEqual(555,parameters.GetProperty("retake_seed").GetInt32());
        Assert.AreEqual(.25,parameters.GetProperty("retake_variance").GetDouble());
        MusicAceIdeaPipeline.Confirm(use,JsonSerializer.Serialize(new{effective=parameters,retake_adapter=new{applied=true}}));
        Assert.Throws<InvalidDataException>(()=>MusicAceIdeaPipeline.Confirm(use,JsonSerializer.Serialize(new{effective=parameters})));
        Assert.Throws<InvalidDataException>(()=>MusicAceIdeaPipeline.Confirm(use,"{\"effective\":{\"audio_codes\":\"wrong\"}}"));
        var zero=use with {Variance=0};MusicAceIdeaPipeline.Confirm(zero,JsonSerializer.Serialize(new{effective=new{audio_codes=idea.Data.Codes,retake_variance=0}}));
    }
    [TestMethod]
    public void PreviewIsBoundedAndMalformedCodeStreamsAreRejected() {
        var idea=Idea();var use=new MusicAceIdeaUse(idea,Preview:true);use.Validate();
        Assert.HasCount(100,use.EffectiveData.Tokens());Assert.AreEqual(20,use.EffectiveData.Duration);
        foreach(var codes in new[]{"bad",idea.Data.Codes+"injected",idea.Data.Codes.Replace("123","64000"),string.Concat(Enumerable.Repeat("<|audio_code_1|>",1801))})
            Assert.Throws<InvalidDataException>(()=>(idea.Data with {Codes=codes}).Validate());
    }
    [TestMethod]
    public void ChangedInputsAndConflictingSourcesCannotSilentlyUseAnOldIdea() {
        using var files=new MusicProjectTests.Files();var jobs=new MusicGenerationJobs(Path.Combine(files.Root,"jobs"));var idea=Idea();
        var job=jobs.Create(files.Root,Path.Combine(files.Root,"out"),"Idea",1,30,"pop",idea.Input.Lyrics,expert:idea.Input.Expert);
        var captured=MusicAceIdeaPipeline.Capture(job,Workspace(idea) with {Retake=true});
        Assert.AreEqual(441,captured.Variants[0].SoundSeed);Assert.IsNotNull(captured.Variants[0].AceIdea!.RetakeSeed);
        Assert.Throws<InvalidDataException>(()=>MusicAceIdeaPipeline.Capture(job with {Lyrics="changed"},Workspace(idea)));
        Assert.Throws<InvalidDataException>(()=>MusicAceIdeaPipeline.Capture(job with {AudioTask=new(){Enabled=true,Source=new("x")}},Workspace(idea)));
    }
    [TestMethod]
    public async Task SelectedIdeasSurviveJobsAndCompletedAudioIsNotRenderedAgain() {
        using var files=new MusicProjectTests.Files();var jobs=new MusicGenerationJobs(Path.Combine(files.Root,"jobs"));var idea=Idea();
        var job=jobs.Create(files.Root,Path.Combine(files.Root,"out"),"Idea",2,30,"pop",idea.Input.Lyrics,expert:idea.Input.Expert);
        job=MusicAceIdeaPipeline.Capture(job,Workspace(idea) with {Retake=true});jobs.Save(job);
        var worker=new Synth();var runner=new MusicGenerationRunner(jobs,worker);await runner.RunAsync(job.Id,default);await runner.RunAsync(job.Id,default);
        Assert.AreEqual(2,worker.Synths);Assert.AreEqual(0,worker.Plans);var completed=jobs.Load(job.Id);
        Assert.IsTrue(completed.Variants.All(v=>v.Completed));var origin=MusicAceIdeaPipeline.FromSong(completed,0);
        Assert.AreEqual(completed.Id,origin.SourceJob);Assert.AreEqual(441,origin.BaseSoundSeed);Assert.AreEqual(idea.Hash,origin.Hash);
        var metadata=MusicSongMetadata.Create(completed,completed.Variants[0],0)["LOPATA_ACE_IDEA"];
        StringAssert.Contains(metadata,"retake");Assert.IsFalse(metadata.Contains(files.Root,StringComparison.OrdinalIgnoreCase));
    }
    [TestMethod]
    public void ExistingSongsWithAutomaticMetadataUseTheirRecordedCotValues() {
        using var files=new MusicProjectTests.Files();var jobs=new MusicGenerationJobs(Path.Combine(files.Root,"jobs"));var idea=Idea();
        var job=jobs.Create(files.Root,Path.Combine(files.Root,"out"),"Auto",1,20,"pop",idea.Input.Lyrics,expert:idea.Input.Expert);
        var variant=job.Variants[0] with {ExecutionReceipt=JsonSerializer.Serialize(new{effective=new{
            audio_codes=string.Concat(idea.Data.Tokens().Take(100)),bpm=(int?)null,cot_bpm=120,keyscale="",cot_keyscale="C major",
            timesignature="",cot_timesignature="4",duration=-1,cot_duration=20,caption="pop",lyrics=idea.Input.Lyrics}})};
        var restored=MusicAceIdeaPipeline.FromSong(job with {Variants=[variant]},0);
        Assert.AreEqual(120,restored.Data.Bpm);Assert.AreEqual("C major",restored.Data.Keyscale);Assert.AreEqual("4",restored.Data.Timesignature);
        Assert.AreEqual(20,restored.Data.Duration);Assert.AreEqual(20,restored.Input.DurationSeconds);
    }
    [TestMethod]
    public void PlanOnlyProjectsAndLegacySnapshotsRemainCompatible() {
        using var files=new MusicProjectTests.Files();var projects=new MusicProjects(Path.Combine(files.Root,"projects"));var idea=Idea();
        var saved=projects.SaveWorkspace(projects.CreateDraft(),idea.Input with {AceIdeas=Workspace(idea)},persistDraft:true);
        Assert.HasCount(0,saved.Steps);Assert.AreEqual(idea.Hash,projects.Load(saved.Id).Saved.AceIdeas.Ideas[0].Hash);
        Assert.IsFalse(JsonSerializer.Deserialize<MusicProjectSnapshot>("{}")!.AceIdeas.Enabled);
    }
    [TestMethod]
    public async Task PausedIdeaQueueReloadsOnlyItsUnfinishedCandidate() {
        using var files=new MusicProjectTests.Files();using var lifetime=new CancellationTokenSource();var store=new MusicAceIdeaStore(Path.Combine(files.Root,"ideas"));
        var background=new BackgroundOperationStore(Path.Combine(files.Root,"background.json"));
        var previous=ApplicationBackgroundOperations.Current;var exit=ApplicationBackgroundOperations.ExitToken;
        void Host(BackgroundOperationController? c,CancellationToken token=default){typeof(ApplicationBackgroundOperations).GetProperty(nameof(ApplicationBackgroundOperations.Current))!.SetValue(null,c);
            typeof(ApplicationBackgroundOperations).GetProperty(nameof(ApplicationBackgroundOperations.ExitToken))!.SetValue(null,token);}
        try {
            var work=new MusicAceIdeaWorkRequest(Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"),files.Root,"pop",Idea().Input,[new(1),new(2)]);
            var controller=new BackgroundOperationController(background);Host(controller,lifetime.Token);var first=new Planner{DelaySecond=true};
            var running=MusicAceIdeaWork.RunAsync(work,first,_=>{},lifetime.Token,storage:store);
            await first.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));await controller.PauseAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsNotNull(store.Load(work.Id).Candidates[0].Idea);Assert.IsNull(store.Load(work.Id).Candidates[1].Idea);
            controller.CheckpointForExit();lifetime.Cancel();try{await running;}catch(OperationCanceledException){}
            var resumed=new BackgroundOperationController(background);resumed.Load();Host(resumed);var second=new Planner();
            await MusicAceIdeaWork.RunAsync(store.Load(work.Id),second,_=>{},default,resumed.State,store);
            Assert.AreEqual(1,second.Plans);Assert.IsTrue(store.Load(work.Id).Candidates.All(c=>c.Idea is not null));
        }finally{Host(previous,exit);}
    }
    private sealed class Planner:IMusicYueWorker {
        public int Plans;public bool DelaySecond;public TaskCompletionSource SecondStarted=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event Action<string>? Log{add{}remove{}}
        public async Task PlanAsync(string model,MusicYueRequest request,string requestPath,string planPath,CancellationToken token){
            Plans++;if(DelaySecond&&Plans==2){SecondStarted.TrySetResult();await Task.Delay(Timeout.Infinite,token);}
            await File.WriteAllTextAsync(planPath,JsonSerializer.Serialize(Idea().Data),token);
        }
        public Task SynthesizeAsync(string model,string decoder,MusicYueRequest request,string requestPath,string outputPath,CancellationToken token)=>throw new AssertFailedException("Plan must not synthesize");
    }
    private sealed class Synth:IMusicYueWorker {
        public int Plans,Synths;public string? LastRequestReceipt{get;private set;}
        public event Action<string>? Log{add{}remove{}}
        public Task PlanAsync(string model,MusicYueRequest request,string requestPath,string planPath,CancellationToken token){Plans++;throw new AssertFailedException("Do not replan saved codes");}
        public Task SynthesizeAsync(string model,string decoder,MusicYueRequest request,string requestPath,string outputPath,CancellationToken token){
            Synths++;MusicOutputTests.WriteWave(outputPath);LastRequestReceipt=JsonSerializer.Serialize(new{effective=MusicAceRequest.Build(request)["params"],retake_adapter=new{applied=true}});return Task.CompletedTask;
        }
    }
}
