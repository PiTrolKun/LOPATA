using System.IO;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.SongClip;
using AIHub.Services.VideoEditor;

namespace AIHub.Tests;

[TestClass,DoNotParallelize]
public sealed class SongClipTests
{
    [TestMethod]
    public async Task ProjectOwnsAttachmentsAndAttemptSettingsAreIndependent()
    {
        using var f=new VideoPreparationTests.TestFiles();var store=new SongClipStore(f.File("projects"));var project=new SongClipProject{Name="Test"};
        var source=f.File("source.wav");await File.WriteAllBytesAsync(source,[1,2,3,4]);
        project.Song=await store.ImportAsync(project.Id,source,CancellationToken.None);project.Lyrics="Original lyrics";store.Save(project);
        File.Delete(source);var loaded=store.Load(project.Id);CollectionAssert.AreEqual(new byte[]{1,2,3,4},File.ReadAllBytes(store.Resolve(loaded.Id,loaded.Song)));
        Assert.AreEqual("Original lyrics",loaded.Lyrics);Assert.Throws<InvalidDataException>(()=>store.Folder("../outside"));
        Assert.Throws<InvalidDataException>(()=>store.Resolve(loaded.Id,"../outside"));
        var snapshot=loaded.Settings with{};loaded.Settings.Steps=40;Assert.AreEqual(20,snapshot.Steps);
        Assert.AreEqual(1,store.List().Count);
    }
    [TestMethod]
    public void TimingsPreserveLrcLineBoundariesAndNeverInventWordAlignment()
    {
        var rows=GlitchframeRunner.ParseTimings("[00:01.20]One line\n[00:03.40]Another line");
        using var json=JsonDocument.Parse(JsonSerializer.Serialize(rows));var a=json.RootElement[0];
        Assert.AreEqual("One line",a.GetProperty("word").GetString());Assert.AreEqual(1.2,a.GetProperty("t_start").GetDouble(),.00001);
        Assert.AreEqual(3.4,a.GetProperty("t_end").GetDouble(),.00001);Assert.AreEqual(0,GlitchframeRunner.ParseTimings("No timings").Length);
    }
    [TestMethod]
    public void ComponentsHavePinnedSourcesAndAllNavigationTranslations()
    {
        var parts=GlitchframeComponents.Parts;Assert.AreEqual(3,parts.Length);
        foreach(var p in parts)foreach(var f in p.Files){Assert.AreEqual(64,f.Sha256.Length);Assert.IsTrue(f.SizeBytes>0);Assert.IsTrue(f.SourceUrl.StartsWith("https://",StringComparison.Ordinal));}
        var video=ScenarioNavigationCatalog.Children("creation_video").ToArray();
        Assert.AreEqual(ScenarioNavigationCatalog.VideoGeneration,video[0].Id);Assert.AreEqual(ScenarioNavigationCatalog.SongClip,video[1].Id);
        foreach(var lang in new[]{"ru","en"}){var l=new LocalizationService();l.Load(lang);Assert.AreNotEqual("SongClip.Title",l.T("SongClip.Title"));Assert.AreNotEqual("Cloud.Description.song_clip",l.T("Cloud.Description.song_clip"));}
        Assert.IsTrue(GlitchframeComponents.Read<PinnedTreeFile[]>("LibraryTree").All(p=>p.Path.StartsWith("Lib/",StringComparison.Ordinal)||p.Path.StartsWith("Scripts/",StringComparison.Ordinal)||p.Path.StartsWith("Include/",StringComparison.Ordinal)));
    }
    [TestMethod,TestCategory("InstalledRuntime")]
    public async Task InstalledGlitchframeProducesFullSongAndReusesFinishedResult()
    {
        var root=Environment.GetEnvironmentVariable("LOPATA_GLITCHFRAME_TEST_MODELS");
        var evidence=Environment.GetEnvironmentVariable("LOPATA_GLITCHFRAME_TEST_EVIDENCE");
        if(root is null||evidence is null){Assert.Inconclusive("Explicit installed-runtime opt-in required.");return;}
        Directory.CreateDirectory(evidence);using var audit=ComponentAudit.UseTestStore(Path.Combine(evidence,"audit"),enabled:false);
        var source=Path.Combine(evidence,"synthetic.wav");
        using(var writer=new BinaryWriter(File.Create(source))){const int sr=44100,count=sr*8;writer.Write("RIFF"u8);writer.Write(36+count*2);writer.Write("WAVEfmt "u8);writer.Write(16);writer.Write((short)1);writer.Write((short)1);writer.Write(sr);writer.Write(sr*2);writer.Write((short)2);writer.Write((short)16);writer.Write("data"u8);writer.Write(count*2);for(var i=0;i<count;i++){var t=i/(double)sr;writer.Write((short)(9000*(.4*Math.Sin(2*Math.PI*220*t)+Math.Exp(-(t%.5)*25)*Math.Sin(2*Math.PI*65*t))));}}
        var store=new SongClipStore(Path.Combine(evidence,"projects"));var p=new SongClipProject{Name="Glitchframe mini test"};
        p.Song=await store.ImportAsync(p.Id,source,CancellationToken.None);store.Save(p);
        var attempt=new SongClipAttempt(Guid.NewGuid().ToString("N"),p.Id,p.Song,"",p.Name,"Light and rhythm","[00:00.5]Light and rhythm\n[00:04.0]Music and motion",new(){Width=640,Height=360,Steps=8,Interval=4,Seed=1257},root,8);
        SongClipStore.Atomic(Path.Combine(evidence,"attempt.json"),attempt);
        using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(6));
        using(var interrupt=new CancellationTokenSource()) {
            var stopped=false;
            try {
                await GlitchframeRunner.RunAsync(attempt,store,(_,message)=>{
                    if(!stopped&&message.StartsWith("Keyframe 1/",StringComparison.Ordinal)&&message.EndsWith(" saved",StringComparison.Ordinal)){stopped=true;interrupt.Cancel();}
                },interrupt.Token);
                Assert.Fail("The interruption must stop the first attempt.");
            }catch(OperationCanceledException){Assert.IsTrue(stopped);}
        }
        var attemptFolder=store.Resolve(p.Id,"attempts/"+attempt.Id);
        var firstFrame=Directory.GetFiles(attemptFolder,"*.png",SearchOption.AllDirectories).Single();
        var frameTime=File.GetLastWriteTimeUtc(firstFrame);var frameBytes=await File.ReadAllBytesAsync(firstFrame);
        var result=await GlitchframeRunner.RunAsync(attempt,store,(fraction,message)=>{Console.WriteLine($"{fraction:P0} {message}");},timeout.Token);
        Assert.AreEqual(frameTime,File.GetLastWriteTimeUtc(firstFrame));CollectionAssert.AreEqual(frameBytes,await File.ReadAllBytesAsync(firstFrame));
        Assert.AreEqual(8,result.Seconds,.08);
        var video=store.Resolve(p.Id,result.File);using var probe=await VideoRuntime.ProbeAsync(video,timeout.Token);
        var tag=probe.RootElement.GetProperty("format").GetProperty("tags").GetProperty("LOPATA_VIDEO").GetString()!;
        var facts=JsonSerializer.Deserialize<OutputProvenance>(tag)!;Assert.AreEqual("song-to-clip/glitchframe",facts.Scenario);Assert.AreEqual(2,facts.Models.Length);Assert.IsFalse(tag.Contains("H:\\",StringComparison.Ordinal));
        var before=File.GetLastWriteTimeUtc(video);var reused=await GlitchframeRunner.RunAsync(attempt,store,(_,_)=>Assert.Fail("Completed output must not invoke inference again."),timeout.Token);
        Assert.AreEqual(result.File,reused.File);Assert.AreEqual(before,File.GetLastWriteTimeUtc(video));
        p.Results.Add(result);store.Save(p);Assert.AreEqual(result.Id,store.Load(p.Id).Results.Single().Id);
        SongClipStore.Atomic(Path.Combine(evidence,"artifacts.json"),new{Video=video,Result=result,Provenance=facts});
    }
}
