using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.SongClip;
using AIHub.Services.VideoEditor;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class GeekatplayInstalledTests
{
    private static string Models => Environment.GetEnvironmentVariable("LOPATA_GEEK_MODELS") ?? throw new InvalidOperationException("Explicit installed model storage required.");
    private static string Evidence => Path.GetFullPath(Environment.GetEnvironmentVariable("LOPATA_GEEK_EVIDENCE") ?? @"H:\AI_HUB\_tmp\songclip-geekatplay-099\physical");
    [TestMethod, TestCategory("InstalledRuntime")]
    public async Task AcquireUsingManagedDownloaderAndExistingConsentOnly()
    {
        if (Environment.GetEnvironmentVariable("LOPATA_GEEK_ACQUIRE") != "1") { Assert.Inconclusive("Explicit LOPATA_GEEK_ACQUIRE=1 required."); return; }
        Directory.CreateDirectory(Evidence);
        using var deadline = new CancellationTokenSource(TimeSpan.FromHours(2));
        var cards = GeekatplayComponents.Cards(Models);
        await ExistingConsentOnly(cards.Select(c => c.ModelArtifactId).ToArray(), deadline.Token);
        var store = new ManagedModelLibraryStore();
        using var acquisition = new ManagedModelAcquisitionService(store) { MaximumParallelConnections = 8 };
        foreach (var card in cards) {
            store.Upsert(card);
            if ((await acquisition.VerifyAsync(card.ModelArtifactId, null, deadline.Token)).Status == ManagedModelStatuses.Installed) continue;
            var progress = new Progress<ManagedModelDownloadProgress>(p => {
                lock (this) File.AppendAllText(Path.Combine(Evidence,"download.jsonl"), JsonSerializer.Serialize(new { utc=DateTimeOffset.UtcNow, p })+Environment.NewLine);
                Console.WriteLine($"{p.ModelArtifactId}: {p.DownloadedBytes}/{p.TotalBytes} · {p.BytesPerSecond:F0} B/s");
            });
            var result = await acquisition.DownloadAsync(card.ModelArtifactId,progress,deadline.Token);
            Assert.AreEqual(ManagedModelStatuses.Installed,result.Status);
        }
    }
    [TestMethod, TestCategory("InstalledRuntime")]
    public async Task InstalledTwoScenesAndCompletedReplayPreserveUserProject()
    {
        if (Environment.GetEnvironmentVariable("LOPATA_GEEK_TEST") != "1") { Assert.Inconclusive("Explicit LOPATA_GEEK_TEST=1 required."); return; }
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(45)); var token=deadline.Token;
        Directory.CreateDirectory(Evidence);
        var sourceStore=new SongClipStore(); const string sourceId="110a48e7559844fbb8d127dc0f14cf98";
        var source=sourceStore.Load(sourceId);
        var before=await Inventory(sourceStore.Folder(sourceId),token);
        var fixture=Path.Combine(Evidence,"ids.json");
        var ids=File.Exists(fixture)?JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(fixture,token))!:[Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N")];
        if(!File.Exists(fixture))SongClipStore.Atomic(fixture,ids);
        var store=new SongClipStore(Path.Combine(Evidence,"projects"));
        await VideoRuntime.PrepareAsync(token);
        var crop=Path.Combine(Evidence,"song-10s.wav");
        if(!File.Exists(crop))await VideoRuntime.RunAsync(false,["-nostdin","-v","error","-y","-i",sourceStore.Resolve(sourceId,source.Song),
            "-ss","19.84","-t","10","-map","0:a:0","-vn","-c:a","pcm_s16le","-ar","44100","-ac","2",crop],token);
        var song=await store.ImportAsync(ids[0],crop,token);
        var references=new List<SongClipReference>();
        foreach(var r in SongClipReferences.For(source).Take(Environment.GetEnvironmentVariable("LOPATA_GEEK_NO_REFERENCES") == "1" ? 0 : 1)) {
            var file=await store.ImportAsync(ids[0],sourceStore.Resolve(sourceId,r.File),token);
            references.Add(new(file,r.Kind.Length>0?r.Kind:"performer",r.Description.Length>0?r.Description:"Певица исполняет песню, а рядом происходят события о котах."));
        }
        var settings=new SongClipSettings{Width=608,Height=352,Fps=24,Quality=85,Seed=742,
            Prompt=source.Settings.Prompt,Geek=new(){Width=608,Height=352,Steps=12,CardSteps=16,ClipSeconds=5}};
        var attempt=new SongClipAttempt(ids[1],ids[0],song,references.FirstOrDefault()?.File??"","Geekatplay · Песня о котах",source.Lyrics,
            "[00:00.00]Проснулся в три ночи, луна — как миска молока",settings,Models,8){Model=GeekatplayRunner.ModelId,PlanningVersion=1,References=references.ToArray()};
        store.EnsureAttemptSnapshot(attempt);
        var old=ComponentLicenseGate.ConfirmAsync;ComponentLicenseGate.ConfirmAsync=ExistingConsentOnly;
        try {
            if (Environment.GetEnvironmentVariable("LOPATA_GEEK_CARDS_ONLY") == "1") {
                await CardsOnly(attempt,store,token); return;
            }
            var watch=Stopwatch.StartNew();
            var result=await GeekatplayRunner.RunAsync(attempt,store,(value,message)=>{
                lock(this)File.AppendAllText(Path.Combine(Evidence,"host-progress.jsonl"),JsonSerializer.Serialize(new{utc=DateTimeOffset.UtcNow,value,message})+Environment.NewLine);
                Console.WriteLine($"{value:P1} {message}");
            },token);
            var elapsed=watch.Elapsed.TotalSeconds;
            var path=store.Resolve(ids[0],result.File);using var probe=await VideoRuntime.ProbeAsync(path,token);
            Assert.AreEqual(GeekatplayRunner.ModelId,result.Model);Assert.AreEqual(10,result.Seconds,.1);
            var job=Path.GetDirectoryName(path)!;
            using var state=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(job,"geek-state.json"),token));
            Assert.AreEqual(3,state.RootElement.GetProperty("cards").GetArrayLength());Assert.AreEqual(2,state.RootElement.GetProperty("clips").GetArrayLength());
            var accepted=await Inventory(job,token);
            var replay=await GeekatplayRunner.RunAsync(attempt,store,(_,_)=>{},token);
            Assert.AreEqual(result.File,replay.File);
            var afterReplay=await Inventory(job,token);
            foreach(var item in accepted.Where(p=>p.Key.EndsWith(".png")||p.Key.EndsWith(".mp4")||p.Key.EndsWith("geek-state.json")))
                Assert.AreEqual(item.Value,afterReplay[item.Key],"Accepted artifact changed on replay: "+item.Key);
            SongClipStore.Atomic(Path.Combine(Evidence,"result.json"),new{result,path,elapsed,probe=probe.RootElement.Clone(),sourceFiles=before.Count,replay=true});
        }
        finally {ComponentLicenseGate.ConfirmAsync=old;CollectionAssert.AreEquivalent(before.ToArray(),(await Inventory(sourceStore.Folder(sourceId),token)).ToArray(),"User source files changed.");}
    }
    private static async Task CardsOnly(SongClipAttempt attempt,SongClipStore store,CancellationToken token)
    {
        var cards=GeekatplayComponents.Cards(Models);
        await ExistingConsentOnly(cards.Select(c=>c.ModelArtifactId).ToArray(),token);
        var library=new ManagedModelLibraryStore();using var acquire=new ManagedModelAcquisitionService(library);
        foreach(var card in cards.Where(c=>c.ModelArtifactId!=GeekatplayComponents.ModelId)) {
            library.Upsert(card);Assert.AreEqual(ManagedModelStatuses.Installed,(await acquire.VerifyAsync(card.ModelArtifactId,null,token)).Status);
            await RuntimeFileAudit.VerifyAsync("SongClip.Geek."+card.ModelArtifactId,card.InstallDirectory,card.Revision,
                card.Files.Select(f=>new PinnedTreeFile(f.RelativePath,f.SizeBytes,f.Sha256)).ToArray(),token);
        }
        var source=await GeekatplayComponents.PrepareSourceAsync(cards.Single(c=>c.ModelArtifactId==GeekatplayComponents.SourceId),token);
        var ac=cards.Single(c=>c.ModelArtifactId==H3MontageComponents.AnalysisLibrariesId);
        var analysis=await GlitchframeComponents.InstallTreeAsync(ac,GlitchframeComponents.Read<PinnedTreeFile[]>("LibraryTree"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AI_HUB","SongClip","Runtime","libraries",ac.Revision),true,token,true);
        var h3=await AIHub.Services.VideoPreparation.VideoH3Runtime.PrepareAsync(Models,8,token,Console.WriteLine,requireReliableGpuProbe:true,
            profile:cards.Where(c=>c.ModelArtifactId.StartsWith("video-h3-",StringComparison.Ordinal)).ToArray());
        var environment=new GeekatplayEnvironment(h3,source,Path.Combine(analysis,"Lib","site-packages"),
            cards.Single(c=>c.ModelArtifactId=="song-clip-glitch-sdxl").InstallDirectory,cards);
        var job=store.Resolve(attempt.ProjectId,"attempts/"+attempt.Id);
        await GeekatplayBinding.EnsureAsync(attempt,store,job,cards,token);
        await RuntimeFileAudit.VerifyAsync("SongClip.Geek.Adapter",Path.Combine(AppContext.BaseDirectory,"Tools"),"cards-probe",GeekatplayBinding.AdapterFiles(),token);
        var payload=GeekatplayRunner.Request(attempt,store,job,environment);
        var path=Path.Combine(job,"geek-analysis-request.json");SongClipStore.Atomic(path,payload);
        await H3MusicRunner.RunWorkerAsync(h3.Python.Python,path,job,(_,message)=>Console.WriteLine(message),token,worker:"song_clip_geek.py",action:"--analyze");
        using var data=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(job,"geek-analysis.json"),token));
        var slots=GeekatplayRunner.Slots(attempt,data.RootElement);
        var plan=await SongClipPlanning.EnsureAsync(attempt,store,job,(_,message)=>Console.WriteLine(message),token,slots);
        payload["visualPlan"]=System.Text.Json.JsonSerializer.SerializeToNode(plan);
        path=Path.Combine(job,"request.json");SongClipStore.Atomic(path,payload);
        await H3MusicRunner.RunWorkerAsync(h3.Python.Python,path,job,(_,message)=>Console.WriteLine(message),token,worker:"song_clip_geek.py",action:"--cards");
        Assert.AreEqual(slots.Length+1,Directory.GetFiles(Path.Combine(job,"cards"),"card_*.png").Length);
        SongClipStore.Atomic(Path.Combine(Evidence,"cards-result.json"),new{job,cards=slots.Length+1,stage="SDXL-cards-only"});
    }
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
        foreach(var id in ids){var entry=service.Entries.Single(e=>e.Id==id);if(!receipts.Any(r=>r.Id==id&&r.Terms==entry.Terms))
            throw new InvalidOperationException("Existing user consent required; installed tests never accept terms: "+id);}
        return Task.CompletedTask;
    }
}
