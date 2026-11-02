using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHub.Models;
using AIHub.Services.SongClip;

namespace AIHub.Tests;

[TestClass]
public sealed class InfiniteTalkRunnerTests
{
    [TestMethod]
    public void DefaultPerformerConfigurationIsValidWithoutLoadingModels()
    {
        var attempt=Attempt();InfiniteTalkRunner.ValidateSettings(attempt);
        Assert.AreEqual(40,attempt.Settings.InfiniteTalk!.Steps);
        Assert.AreEqual(81,attempt.Settings.InfiniteTalk.FrameWindow);
        Assert.AreEqual(9,attempt.Settings.InfiniteTalk.MotionFrames);
        Assert.AreEqual(24,attempt.Settings.Fps);
    }

    [TestMethod]
    [DataRow("model")][DataRow("planning")][DataRow("output-fps")][DataRow("seed")]
    [DataRow("width")][DataRow("native-grid")][DataRow("height")][DataRow("steps")][DataRow("window")]
    [DataRow("overlap")][DataRow("audio-nan")][DataRow("reserve-infinite")]
    public void InvalidSettingsAreRejectedBeforeAWorkerCanStart(string defect)
    {
        var attempt=Attempt();var settings=attempt.Settings;var native=settings.InfiniteTalk!;
        attempt=defect switch {
            "model"=>attempt with{Model="h3_music_context"},
            "planning"=>attempt with{PlanningVersion=0,References=null},
            "output-fps"=>attempt with{Settings=settings with{Fps=25}},
            "seed"=>attempt with{Settings=settings with{Seed=-1}},
            _=>attempt with{Settings=settings with{InfiniteTalk=defect switch {
                "width"=>native with{Width=450},"native-grid"=>native with{Width=272},"height"=>native with{Height=128},
                "steps"=>native with{Steps=19},"window"=>native with{FrameWindow=82},
                "overlap"=>native with{MotionFrames=10},"audio-nan"=>native with{AudioCfg=double.NaN},
                _=>native with{VramReserveGb=double.PositiveInfinity}}}}
        };
        Assert.Throws<InvalidDataException>(()=>InfiniteTalkRunner.ValidateSettings(attempt));
    }

    [TestMethod]
    [DataRow("none")][DataRow("world")][DataRow("extra")][DataRow("legacy")]
    public void APerformerCannotBeSilentlyReplacedByAWorldImageOrMultipleFiles(string defect)
    {
        var attempt=Attempt();
        attempt=defect switch {
            "none"=>attempt with{Reference="",References=[]},
            "world"=>attempt with{References=[new(attempt.Reference,"world","The room where the story happens.")]},
            "extra"=>attempt with{References=[..attempt.References!,new("attachments/other.png","object","A clock.")]},
            _=>attempt with{References=null}
        };
        Assert.Throws<InvalidDataException>(()=>InfiniteTalkRunner.ValidateSettings(attempt));
    }

    [TestMethod]
    public void NativeAnalysisKeepsTheWholeSongIncludingTheNonStandardTail()
    {
        var attempt=Attempt();var analysis=Analysis(attempt,20.01);
        using var document=JsonDocument.Parse(analysis.ToJsonString());
        var slots=InfiniteTalkRunner.ValidateAnalysis(attempt,document.RootElement);
        Assert.AreEqual(7,slots.Length);Assert.AreEqual(20.01,slots[^1].EndSeconds);
        for(var index=1;index<slots.Length;index++)
            Assert.AreEqual(9/25d,slots[index-1].EndSeconds-slots[index].StartSeconds,1e-9);
    }

    [TestMethod]
    [DataRow("schema")][DataRow("duration-zero")][DataRow("duration-too-long")]
    [DataRow("missing-window")][DataRow("wrong-index")][DataRow("wrong-start")][DataRow("truncated-tail")]
    public void IncompleteOrForeignAnalysisCannotReachPlanning(string defect)
    {
        var attempt=Attempt();var analysis=Analysis(attempt,20.01);var slots=analysis["slots"]!.AsArray();
        switch(defect) {
            case "schema":analysis["schema"]=2;break;
            case "duration-zero":analysis["durationSeconds"]=0;break;
            case "duration-too-long":analysis["durationSeconds"]=7201;break;
            case "missing-window":slots.RemoveAt(slots.Count-1);break;
            case "wrong-index":slots[0]!["index"]=2;break;
            case "wrong-start":slots[1]!["startSeconds"]=3.24;break;
            case "truncated-tail":slots[^1]!["endSeconds"]=20;break;
        }
        using var document=JsonDocument.Parse(analysis.ToJsonString());
        Assert.Throws<InvalidDataException>(()=>InfiniteTalkRunner.ValidateAnalysis(attempt,document.RootElement));
    }

    [TestMethod]
    [DataRow("{}")] [DataRow("[]")]
    [DataRow("{\"schema\":1,\"durationSeconds\":\"NaN\",\"slots\":[]}")]
    [DataRow("{\"schema\":1,\"durationSeconds\":1e400,\"slots\":[]}")]
    [DataRow("{\"schema\":1,\"durationSeconds\":1,\"slots\":[{\"index\":1,\"startSeconds\":0,\"endSeconds\":\"NaN\"}]}")]
    [DataRow("{\"schema\":1,\"durationSeconds\":1,\"slots\":[{\"index\":1,\"startSeconds\":0,\"endSeconds\":1e400}]}")]
    public void MalformedAnalysisProducesTheSameControlledFailure(string json)
    {
        using var document=JsonDocument.Parse(json);
        var failure=Assert.Throws<InvalidDataException>(()=>InfiniteTalkRunner.ValidateAnalysis(Attempt(),document.RootElement));
        Assert.AreEqual("SongClip.InfiniteResultInvalid",failure.Message);
    }

    [TestMethod]
    public void RequestRoutesEachNativeLoaderToItsAuditedPhysicalDirectory()
    {
        using var files=new VideoPreparationTests.TestFiles();var attempt=Attempt();var store=new SongClipStore(files.File("projects"));
        var root=files.File("models");var cards=InfiniteTalkComponents.Cards(root);
        var selection=new AIHub.Services.ManagedPythonSelection(null!,AIHub.Services.PythonRuntimeProfile.Cuda128,files.File("python.exe"),"cuda:1",16L*1024*1024*1024,"fixture");
        var environment=new InfiniteTalkEnvironment(selection,files.File("libraries"),files.File("comfy"),files.File("wrapper"),files.File("extras"),cards);
        var request=InfiniteTalkRunner.Request(attempt,store,store.Resolve(attempt.ProjectId,"attempts/"+attempt.Id),environment);
        var known=SongClipVariantCatalog.DescribeFiles(cards);var folders=request["modelFolders"]!.AsObject();
        foreach(var (key,category) in new[]{("model","diffusion_models"),("adapter","diffusion_models"),("encoder","text_encoders"),
            ("vae","vae"),("vision","clip_vision"),("wav2vec","wav2vec2")}) {
            var name=request[key]!.GetValue<string>();var actual=folders[category]!.AsArray().Select(p=>p!.GetValue<string>()).ToArray();
            var expected=Path.GetDirectoryName(known.Single(f=>Path.GetFileName(f.Path)==name).Path)!;
            CollectionAssert.Contains(actual,expected);
            Assert.IsTrue(Path.IsPathFullyQualified(expected));Assert.AreEqual(name,Path.GetFileName(name));
        }
        Assert.HasCount(2,folders["diffusion_models"]!.AsArray());
        Assert.AreEqual(40,request["steps"]!.GetValue<int>());Assert.AreEqual(81,request["frameWindow"]!.GetValue<int>());
        Assert.AreEqual(9,request["motionFrames"]!.GetValue<int>());Assert.AreEqual("cuda:1",request["device"]!.GetValue<string>());
        Assert.AreEqual(store.Resolve(attempt.ProjectId,attempt.Reference),request["reference"]!.GetValue<string>());
        Assert.AreEqual(attempt.References![0].Description,request["referenceRole"]!.GetValue<string>());
        Assert.IsFalse(cards.SelectMany(c=>c.Files).Any(f=>f.RelativePath.Contains("vace",StringComparison.OrdinalIgnoreCase)));
        Assert.AreEqual(cards.Length,request["pins"]!.AsObject().Count);
    }

    [TestMethod]
    public async Task BindingIgnoresVerificationTimestampsButRejectsChangedInputsAndPinnedContent()
    {
        using var files=new VideoPreparationTests.TestFiles();var store=new SongClipStore(files.File("projects"));var attempt=Attempt();
        var song=store.Resolve(attempt.ProjectId,attempt.Song);var image=store.Resolve(attempt.ProjectId,attempt.Reference);
        Directory.CreateDirectory(Path.GetDirectoryName(song)!);
        await File.WriteAllTextAsync(song,"source audio fixture");await File.WriteAllTextAsync(image,"performer fixture");
        var job=store.Resolve(attempt.ProjectId,"attempts/"+attempt.Id);Directory.CreateDirectory(job);store.EnsureAttemptSnapshot(attempt);
        var file=new ManagedModelArtifactFile{RelativePath="model.gguf",SizeBytes=5,Sha256="abc",IsRequired=true};
        ManagedModelArtifactCard[] cards=[new(){ModelArtifactId="test",Revision="pinned",Files=[file]}];
        await InfiniteTalkBinding.EnsureAsync(attempt,store,job,cards,CancellationToken.None);
        var path=Path.Combine(job,"infinitetalk-input-binding.json");var saved=await File.ReadAllTextAsync(path);
        file.VerifiedSizeBytes=5;file.VerifiedLastWriteTimeUtc=DateTimeOffset.UtcNow;
        await InfiniteTalkBinding.EnsureAsync(attempt,store,job,cards,CancellationToken.None);
        Assert.AreEqual(saved,await File.ReadAllTextAsync(path));
        await Assert.ThrowsAsync<InvalidDataException>(()=>InfiniteTalkBinding.EnsureAsync(attempt with {
            Settings=attempt.Settings with{InfiniteTalk=attempt.Settings.InfiniteTalk! with{AudioCfg=3}}},store,job,cards,CancellationToken.None));
        await File.WriteAllTextAsync(image,"different performer fixture");
        await Assert.ThrowsAsync<InvalidDataException>(()=>InfiniteTalkBinding.EnsureAsync(attempt,store,job,cards,CancellationToken.None));
        await File.WriteAllTextAsync(image,"performer fixture");file.Sha256="changed";
        await Assert.ThrowsAsync<InvalidDataException>(()=>InfiniteTalkBinding.EnsureAsync(attempt,store,job,cards,CancellationToken.None));
        Assert.AreEqual(saved,await File.ReadAllTextAsync(path));
        Assert.IsFalse(Directory.EnumerateFiles(job).Any(p=>Path.GetFileName(p)=="request.json"));
    }

    [TestMethod]
    public async Task BindingCannotAdoptExistingUnboundModelWork()
    {
        using var files=new VideoPreparationTests.TestFiles();var store=new SongClipStore(files.File("projects"));var attempt=Attempt();
        var song=store.Resolve(attempt.ProjectId,attempt.Song);Directory.CreateDirectory(Path.GetDirectoryName(song)!);
        await File.WriteAllTextAsync(song,"source audio fixture");
        await File.WriteAllTextAsync(store.Resolve(attempt.ProjectId,attempt.Reference),"performer fixture");
        var job=store.Resolve(attempt.ProjectId,"attempts/"+attempt.Id);Directory.CreateDirectory(job);
        await File.WriteAllTextAsync(Path.Combine(job,"worker-result.json"),"{}");
        await Assert.ThrowsAsync<InvalidDataException>(()=>InfiniteTalkBinding.EnsureAsync(attempt,store,job,[],CancellationToken.None));
        Assert.IsFalse(File.Exists(Path.Combine(job,"infinitetalk-input-binding.json")));
    }

    private static JsonObject Analysis(SongClipAttempt attempt,double duration)=>new() {
        ["schema"]=1,["durationSeconds"]=duration,["slots"]=JsonSerializer.SerializeToNode(SongClipVisualProtocol.Slots(attempt,duration)
            .Select(s=>new{index=s.Index,startSeconds=s.StartSeconds,endSeconds=s.EndSeconds}))
    };
    private static SongClipAttempt Attempt()=>new(Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"),
        "attachments/song.wav","attachments/singer.png","Song","The cat watches the moon.","[00:03.00]The cat watches the moon.",
        new(){Seed=12,InfiniteTalk=new()},Path.Combine(Path.GetTempPath(),"model-store"),4) {
        Model=InfiniteTalkRunner.ModelId,PlanningVersion=1,
        References=[new("attachments/singer.png","performer","The singer performs the song.")]
    };
}
