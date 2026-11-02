using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.SongClip;
using AIHub.Services.VideoEditor;

namespace AIHub.Tests;

/// <summary>Explicit physical test only. It never accepts terms or writes to the user's project.</summary>
[TestClass,DoNotParallelize]
public sealed class InfiniteTalkInstalledTests
{
    private const string SourceId="110a48e7559844fbb8d127dc0f14cf98";
    private static string Models=>Path.GetFullPath(Environment.GetEnvironmentVariable("LOPATA_INFINITE_MODELS")
        ??throw new InvalidOperationException("Explicit LOPATA_INFINITE_MODELS storage is required."));
    private static string Evidence=>Path.GetFullPath(Environment.GetEnvironmentVariable("LOPATA_INFINITE_EVIDENCE")
        ??@"H:\AI_HUB\_tmp\songclip-infinitetalk-0102\physical");
    private sealed record Ids(int Schema,string Source,string Project,string Attempt);
    private sealed record Accepted(string Receipt,Dictionary<string,string> Frames);

    [TestMethod,TestCategory("InstalledRuntime"),Timeout(2_800_000)]
    public Task TwoNativeWindowsPauseResumeAndCompletedReplayPreserveTheUserSong()=>RunScenario(baseline:false);

    [TestMethod,TestCategory("InstalledRuntime"),Timeout(2_800_000)]
    public Task OneAuthorBaselineWindowPreservesTheUserSongAndCompletedReplay()=>RunScenario(baseline:true);

    private async Task RunScenario(bool baseline)
    {
        var expectedSeconds=baseline?3:6;var expectedWindows=baseline?1:2;
        var expectedSteps=baseline?40:20;var expectedNativeFrames=baseline?81:153;
        if(Environment.GetEnvironmentVariable("LOPATA_INFINITE_TEST")!="1") {
            Assert.Inconclusive("Explicit LOPATA_INFINITE_TEST=1 is required for model inference.");return;
        }
        using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(45));var token=deadline.Token;
        Directory.CreateDirectory(Evidence);var sourceStore=new SongClipStore();var source=sourceStore.Load(SourceId);
        var before=await Inventory(sourceStore.Folder(SourceId),token);
        var auditEnabled=ComponentAudit.Enabled;
        var consent=ComponentLicenseGate.ConfirmAsync;ComponentLicenseGate.ConfirmAsync=ExistingConsentOnly;
        try {
            if(File.Exists(AppDataPaths.SettingsPath)) {
                var settings=JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(AppDataPaths.SettingsPath,token),
                    new JsonSerializerOptions{PropertyNameCaseInsensitive=true})??throw new InvalidDataException("Invalid application settings.");
                ComponentAudit.Configure(settings.Behavior.ComponentAuditEnabled);
            }
            var cards=InfiniteTalkComponents.Cards(Models);await ExistingConsentOnly(cards.Select(c=>c.ModelArtifactId).ToArray(),token);
            var environment=await InfiniteTalkComponents.PrepareAsync(Models,8,token,Console.WriteLine);
            var idsFile=Path.Combine(Evidence,"fixture-ids.json");
            var ids=File.Exists(idsFile)?JsonSerializer.Deserialize<Ids>(await File.ReadAllTextAsync(idsFile,token))!
                :new(1,SourceId,Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"));
            if(!File.Exists(idsFile))SongClipStore.Atomic(idsFile,ids);
            Assert.AreEqual(1,ids.Schema);Assert.AreEqual(SourceId,ids.Source);
            var store=new SongClipStore(Path.Combine(Evidence,"projects"));
            Assert.AreNotEqual(sourceStore.Root,store.Root);
            var job=store.Resolve(ids.Project,"attempts/"+ids.Attempt);var snapshot=Path.Combine(job,"snapshot.json");
            SongClipAttempt attempt;
            if(File.Exists(snapshot)) {
                attempt=JsonSerializer.Deserialize<SongClipAttempt>(await File.ReadAllTextAsync(snapshot,token))!;
                Assert.AreEqual(ids.Project,attempt.ProjectId);Assert.AreEqual(ids.Attempt,attempt.Id);
                Assert.AreEqual(Models,attempt.ModelsRoot);InfiniteTalkRunner.ValidateSettings(attempt);
                Assert.AreEqual(expectedSteps,attempt.Settings.InfiniteTalk!.Steps,
                    "An existing physical fixture belongs to a different sampling contract; use a separate evidence directory.");
            } else {
                await VideoRuntime.PrepareAsync(token);
                var crop=await Crop(sourceStore.Resolve(SourceId,source.Song),expectedSeconds,token);
                var song=await store.ImportAsync(ids.Project,crop,token);
                var audited=RuntimeFileAudit.Contained(environment.Wrapper,"example_workflows/example_inputs/woman.jpg");
                var reference=Environment.GetEnvironmentVariable("LOPATA_INFINITE_REFERENCE")??audited;
                Assert.AreEqual(await H3MontageBinding.HashAsync(audited,token),await H3MontageBinding.HashAsync(reference,token),
                    "An explicit portrait override must be the same pinned upstream test image.");
                var image=await store.ImportAsync(ids.Project,reference,token);
                var settings=new SongClipSettings {Width=448,Height=448,Fps=24,Quality=85,Seed=742,
                    Prompt="A realistic continuous music performance. Keep the same referenced singer visibly performing, with natural restrained gestures and one coherent camera view.",
                    InfiniteTalk=new(){Width=448,Height=448,Steps=expectedSteps}};
                attempt=new(ids.Attempt,ids.Project,song,image,"InfiniteTalk · Song of Cats · "+expectedSeconds+"-second test",source.Lyrics,
                    source.Timings,settings,Models,8) {Model=InfiniteTalkRunner.ModelId,PlanningVersion=1,
                    References=[new(image,"performer","The singer in this photograph performs the song throughout the clip.")]};
                var cues=SongClipVisualProtocol.Source(attempt).Timings.Where(c=>c.StartSeconds>=19.84&&c.StartSeconds<19.84+expectedSeconds);
                attempt=attempt with{Timings=string.Join("\n",cues.Select(c=>"["+TimeSpan.FromSeconds(c.StartSeconds-19.84).ToString(@"mm\:ss\.fff",CultureInfo.InvariantCulture)+"]"+c.Text))};
                store.EnsureAttemptSnapshot(attempt);
                store.Save(new(){Id=ids.Project,Name=attempt.Name,Song=song,Reference=image,References=attempt.References,
                    Lyrics=attempt.Lyrics,Timings=attempt.Timings,Model=attempt.Model,Settings=settings,PendingAttemptId=attempt.Id});
            }
            var immutable=await File.ReadAllBytesAsync(snapshot,token);var watch=Stopwatch.StartNew();
            Accepted? accepted=null;
            if(!baseline) {
                var acceptedFile=Path.Combine(Evidence,"accepted-first-window.json");
                if(!File.Exists(acceptedFile))await PauseAtFirstWindow(attempt,store,job,token);
                accepted=File.Exists(acceptedFile)?JsonSerializer.Deserialize<Accepted>(await File.ReadAllTextAsync(acceptedFile,token))!
                    :await FirstWindow(job,token);
                if(!File.Exists(acceptedFile))SongClipStore.Atomic(acceptedFile,accepted);
                await CheckFirstWindow(job,accepted,token);
            }
            var result=await InfiniteTalkRunner.RunAsync(attempt,store,Report,token);
            var output=store.Resolve(ids.Project,result.File);using var probe=await VideoRuntime.ProbeAsync(output,token);
            using var input=await VideoRuntime.ProbeAsync(store.Resolve(ids.Project,attempt.Song),token);
            var plan=JsonSerializer.Deserialize<SongClipVisualPlan>(await File.ReadAllTextAsync(Path.Combine(job,"visual-plan.json"),token))!;
            Assert.AreEqual(expectedWindows,plan.Scenes.Length);Assert.AreEqual("executor",SongClipPlannerModel.ModelIdFor(attempt.Model));
            Assert.IsTrue(plan.Assistants.Any(m=>m.Name.Contains("27B",StringComparison.OrdinalIgnoreCase)
                &&m.Role=="song-to-clip semantic planning"),"The installed executor must supply recorded real planning provenance.");
            var seconds=H3MusicResultValidation.Validate(probe.RootElement,input.RootElement,attempt.Settings,attempt.Id,
                [..InfiniteTalkRunner.Facts(environment.Cards),..plan.Assistants],"song-to-clip/infinitetalk");
            Assert.AreEqual(expectedSeconds,seconds,.1);Assert.AreEqual(expectedSeconds,result.Seconds,.1);Assert.AreEqual(InfiniteTalkRunner.ModelId,result.Model);
            Assert.AreEqual(expectedWindows,Directory.GetFiles(Path.Combine(job,"infinite-checkpoints"),"*.json").Length);
            Assert.AreEqual(expectedNativeFrames,Directory.GetFiles(Path.Combine(job,"infinite-frames"),"frame_*.png").Length);
            using var receipt=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(job,"worker-result.json"),token));
            var nativeFramesUsed=receipt.RootElement.GetProperty("frames").GetInt32();
            Assert.AreEqual(expectedSeconds*25,nativeFramesUsed);
            Assert.AreEqual(25,receipt.RootElement.GetProperty("fps").GetInt32());
            var encodedVideo=probe.RootElement.GetProperty("streams").EnumerateArray()
                .Single(stream=>stream.GetProperty("codec_type").GetString()=="video");
            var encodedOutputFrames=int.Parse(encodedVideo.GetProperty("nb_frames").GetString()!,CultureInfo.InvariantCulture);
            Assert.AreEqual(expectedSeconds*24,encodedOutputFrames,"The encoded output must contain 24 frames per second of the complete song.");
            Assert.IsTrue(receipt.RootElement.GetProperty("referenceUsed").GetBoolean());
            Assert.AreEqual(await H3MontageBinding.HashAsync(store.Resolve(ids.Project,attempt.Song),token),receipt.RootElement.GetProperty("sourceSha256").GetString());
            if(accepted is not null)await CheckFirstWindow(job,accepted,token);
            CollectionAssert.AreEqual(immutable,await File.ReadAllBytesAsync(snapshot,token));
            var audio=await CheckOriginalAudio(store.Resolve(ids.Project,attempt.Song),output,token);
            var material=(await Inventory(job,token)).Where(p=>ImmutableMaterial(p.Key)).ToDictionary(p=>p.Key,p=>p.Value);
            var replayEvents=new List<string>();
            var replay=await InfiniteTalkRunner.RunAsync(attempt,store,(fraction,message)=>{replayEvents.Add(message);Report(fraction,message);},token);
            Assert.AreEqual(result.File,replay.File);
            var afterReplay=await Inventory(job,token);
            foreach(var file in material)Assert.AreEqual(file.Value,afterReplay[file.Key],"Accepted artifact changed on replay: "+file.Key);
            Assert.IsFalse(replayEvents.Where(m=>m.StartsWith('{')).Any(m=>{
                using var message=JsonDocument.Parse(m);var value=message.RootElement;
                return value.TryGetProperty("kind",out var kind)&&(kind.GetString()=="progress"
                    ||kind.GetString()=="stage"&&value.TryGetProperty("stage",out var stage)&&stage.GetString()=="infinitetalk-window");
            }),"Completed replay entered native sampling again.");
            SongClipStore.Atomic(Path.Combine(Evidence,"physical-result.json"),new{result,output,elapsed=watch.Elapsed.TotalSeconds,
                windows=expectedWindows,nativeGeneratedFrames=expectedNativeFrames,nativeFramesUsed,encodedOutputFrames,
                baseline,steps=expectedSteps,originalAudioCorrelations=audio,paused=!baseline,replayed=true,
                sourceFiles=before.Count,probe=probe.RootElement.Clone()});
        }
        finally {
            ComponentLicenseGate.ConfirmAsync=consent;ComponentAudit.Configure(auditEnabled);
            CollectionAssert.AreEquivalent(before.ToArray(),(await Inventory(sourceStore.Folder(SourceId),CancellationToken.None)).ToArray(),
                "The user source project, audio or attachments changed.");
        }
    }

    private async Task PauseAtFirstWindow(SongClipAttempt attempt,SongClipStore store,string job,CancellationToken token)
    {
        Assert.IsFalse(File.Exists(Path.Combine(job,"infinite-checkpoints","000002.json")),
            "The persistent test has completed without its saved pause evidence; keep it for diagnosis.");
        using var pause=CancellationTokenSource.CreateLinkedTokenSource(token);var boundary=false;
        try {
            await InfiniteTalkRunner.RunAsync(attempt,store,(fraction,message)=>{
            Report(fraction,message);if(!message.StartsWith('{'))return;
            using var document=JsonDocument.Parse(message);var value=document.RootElement;
            if(!value.TryGetProperty("kind",out var kind))return;
            // After a machine restart an already committed first window is restored, never sampled again.
            var first=kind.GetString()=="chunk"&&value.GetProperty("index").GetInt32()==1
                ||kind.GetString()=="cached"&&value.TryGetProperty("windows",out var windows)&&windows.GetInt32()==1;
            if(!first||boundary)return;boundary=true;
            SongClipStore.Atomic(Path.Combine(Evidence,"pause-request.json"),new{utc=DateTimeOffset.UtcNow,message});pause.Cancel();
            },pause.Token);
            Assert.Fail("The first native window completed without honoring the requested cancellation.");
        }
        catch(OperationCanceledException) when(pause.IsCancellationRequested) { }
        token.ThrowIfCancellationRequested();Assert.IsTrue(boundary,"Cancellation happened before the first committed window.");
        Assert.IsFalse(OwnedProcessRegistry.Shared.GetSnapshot().Any(p=>p.Component=="SongClip.InfiniteTalk"),
            "The paused owned worker must have exited before continuation is offered.");
        Assert.AreEqual(1,Directory.GetFiles(Path.Combine(job,"infinite-checkpoints"),"*.json").Length);
        Assert.AreEqual(81,Directory.GetFiles(Path.Combine(job,"infinite-frames"),"frame_*.png").Length);
    }

    private static async Task<Accepted> FirstWindow(string job,CancellationToken token)
    {
        var receipt=Path.Combine(job,"infinite-checkpoints","000001.json");
        var frames=Directory.GetFiles(Path.Combine(job,"infinite-frames"),"frame_*.png").Order().Take(81).ToArray();
        Assert.AreEqual(81,frames.Length);using var data=JsonDocument.Parse(await File.ReadAllTextAsync(receipt,token));
        Assert.AreEqual(1,data.RootElement.GetProperty("index").GetInt32());Assert.AreEqual(81,data.RootElement.GetProperty("frameCount").GetInt32());
        Assert.IsFalse(data.RootElement.GetProperty("final").GetBoolean());var hashes=new Dictionary<string,string>();
        foreach(var frame in frames)hashes[Path.GetFileName(frame)]=await H3MontageBinding.HashAsync(frame,token);
        return new(await H3MontageBinding.HashAsync(receipt,token),hashes);
    }
    private static async Task CheckFirstWindow(string job,Accepted accepted,CancellationToken token)
    {
        Assert.AreEqual(accepted.Receipt,await H3MontageBinding.HashAsync(Path.Combine(job,"infinite-checkpoints","000001.json"),token));
        foreach(var frame in accepted.Frames)Assert.AreEqual(frame.Value,await H3MontageBinding.HashAsync(Path.Combine(job,"infinite-frames",frame.Key),token),frame.Key);
    }
    private static async Task<string> Crop(string source,int seconds,CancellationToken token)
    {
        var crop=Path.Combine(Evidence,"song-"+seconds+"s.wav");
        if(!File.Exists(crop)) {
            var pending=Path.Combine(Evidence,"song-"+seconds+"s.pending.wav");
            var facts=new OutputProvenance{Scenario="song-to-clip/infinitetalk/test-fixture",CreatedUtc=DateTimeOffset.UtcNow,
                ModelHistory="User source audio crop; source model history is not inferred."};
            await VideoRuntime.RunAsync(false,["-nostdin","-v","error","-y","-i",source,"-ss","19.84","-t",seconds.ToString(CultureInfo.InvariantCulture),"-map","0:a:0","-vn",
                "-c:a","pcm_s16le","-ar","44100","-ac","2","-metadata","encoded_by=LOPATA "+facts.ProgramVersion,"-metadata","comment="+facts.Json(),pending],token);
            token.ThrowIfCancellationRequested();File.Move(pending,crop);
        }
        using var probe=await VideoRuntime.ProbeAsync(crop,token);
        Assert.AreEqual(seconds,double.Parse(probe.RootElement.GetProperty("format").GetProperty("duration").GetString()!,CultureInfo.InvariantCulture),.001);
        return crop;
    }
    private static async Task<double[]> CheckOriginalAudio(string source,string result,CancellationToken token)
    {
        async Task<float[]> Decode(string file,string name) {
            var target=Path.Combine(Evidence,name+".f32");
            await VideoRuntime.RunAsync(false,["-nostdin","-v","error","-y","-i",file,"-map","0:a:0","-vn","-ac","1","-ar","16000","-f","f32le",target],token);
            var data=await File.ReadAllBytesAsync(target,token);var values=new float[data.Length/sizeof(float)];Buffer.BlockCopy(data,0,values,0,data.Length);return values;
        }
        var original=await Decode(source,"source-audio");var output=await Decode(result,"result-audio");
        Assert.IsTrue(Math.Abs(original.Length-output.Length)<=1600,"The output audio does not have the complete original length.");
        var correlations=new double[3];var sectionLength=Math.Min(original.Length,output.Length)/correlations.Length;
        Assert.IsGreaterThan(640,sectionLength,"The three audio verification regions must contain source audio.");
        for(var section=0;section<3;section++) {
            var start=section*sectionLength+320;var count=sectionLength-640;double best=-1;
            for(var lag=-80;lag<=80;lag++) {double xy=0,xx=0,yy=0;
                for(var i=0;i<count;i+=2){var x=original[start+i];var y=output[start+i+lag];xy+=x*y;xx+=x*x;yy+=y*y;}
                if(xx>1e-8&&yy>1e-8)best=Math.Max(best,xy/Math.Sqrt(xx*yy));
            }
            Assert.IsGreaterThan(.98,best,"The original song differs in audio section "+section);correlations[section]=best;
        }
        return correlations;
    }
    private void Report(double value,string message)
    {
        lock(this)File.AppendAllText(Path.Combine(Evidence,"host-progress.jsonl"),JsonSerializer.Serialize(new{utc=DateTimeOffset.UtcNow,value,message})+Environment.NewLine);
        Console.WriteLine($"{value:P1} {message}");
    }
    private static bool ImmutableMaterial(string name)=>new[]{".png",".mp4",".safetensors"}.Any(name.EndsWith)
        ||new[]{"snapshot.json","request.json","worker-result.json","visual-plan.json","visual-plan-binding.json",
            "infinitetalk-analysis.json","infinitetalk-input-binding.json","infinite-state.json","000001.json","000002.json"}.Contains(Path.GetFileName(name));
    private static async Task<Dictionary<string,string>> Inventory(string folder,CancellationToken token)
    {
        var files=new Dictionary<string,string>();foreach(var file in Directory.EnumerateFiles(folder,"*",SearchOption.AllDirectories))
            files[Path.GetRelativePath(folder,file)]=await H3MontageBinding.HashAsync(file,token);return files;
    }
    private static Task ExistingConsentOnly(IReadOnlyList<string> ids,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var service=new ComponentLicenseService(Path.Combine(AppContext.BaseDirectory,"Licenses"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AI_HUB","Licenses","receipts.json"));
        var receipts=service.ReadReceipts();
        foreach(var id in ids) {var entry=service.Entries.Single(e=>e.Id==id);
            if(!receipts.Any(r=>r.Id==id&&r.Terms==entry.Terms))throw new InvalidOperationException("Existing user consent required; installed tests never accept terms: "+id);}
        return Task.CompletedTask;
    }
}
