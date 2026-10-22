using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.MusicScore;

namespace AIHub.Tests;

[TestClass,DoNotParallelize]
public sealed class MusicScorePhysicalTests
{
    [TestMethod]
    public async Task InstalledStudioPlansAndSingsTheSelectedScoreWithConfirmedTiming() {
        var models=Environment.GetEnvironmentVariable("LOPATA_SCORE_LIVE_MODELS");
        var output=Environment.GetEnvironmentVariable("LOPATA_SCORE_LIVE_OUTPUT");
        if(string.IsNullOrWhiteSpace(models)||string.IsNullOrWhiteSpace(output))Assert.Inconclusive("Explicit opt-in, installed models and a new output folder are required.");
        if(Directory.Exists(output))throw new IOException("Use a fresh technical output directory.");Directory.CreateDirectory(output!);
        using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var settings=MusicModelVariants.Defaults(MusicStudioRuntime.Variation);
        settings.Values["cot"]=0;settings.Values["cfg_scale"]=1;settings.Values["steps"]=8;settings.Values["abc_sampling.max_tokens"]=768;
        settings.Values["semantic_sampling.min_tokens"]=200;settings.Values["semantic_sampling.max_tokens"]=400;
        var lyrics="[Verse]\nЛАПАТА поёт.\n[Chorus]\nМузыка живёт.";
        var worker=new MusicStudioWorker();worker.Log+=line=>File.AppendAllText(Path.Combine(output!,"native.log"),line+"\n");
        var request=new MusicYueRequest("simple acoustic pop, 120 bpm",lyrics,7321,8321,8){Expert=settings,
            StudioRuntimeRevision=MusicStudioRuntime.Current.Revision,StudioRuntimePack=MusicStudioRuntime.Current.Pack,FollowScoreSections=true};
        var model=MusicModelVariants.Artifact(models!,MusicStudioRuntime.Variation,MusicComponentCatalog.ModelId);
        var decoder=MusicModelVariants.Artifact(models!,MusicStudioRuntime.Variation,MusicComponentCatalog.DecoderId);
        var plan=Path.Combine(output!,"generated.abc");
        await worker.PlanAsync(model,request,Path.Combine(output!,"plan-request.json"),plan,deadline.Token);
        var inspection=await MusicScoreNative.InspectAsync(await File.ReadAllTextAsync(plan),deadline.Token);
        File.WriteAllText(Path.Combine(output!,"plan-summary.json"),JsonSerializer.Serialize(inspection));
        Assert.IsFalse(OwnedProcessRegistry.Shared.GetSnapshot().Any(p=>p.Component=="Music.Studio"));
        // Use a different, explicitly selected score so accidental re-planning cannot pass this check.
        var selected=MusicScoreTests.Abc;
        request=request with {Abc=selected,ScoreLyricTiming=true};
        await MusicScorePipeline.CheckSchedulingAsync(request,deadline.Token);
        var audio=Path.Combine(output!,"selected-score.wav");
        await worker.SynthesizeAsync(model,decoder,request,Path.Combine(output!,"synth-request.json"),audio,deadline.Token);
        var duration=MusicWaveFile.ReadDuration(audio);Assert.IsTrue(duration.TotalSeconds>0);
        using var receipt=JsonDocument.Parse(worker.LastRequestReceipt!);
        var effective=receipt.RootElement.GetProperty("generation_settings");
        Assert.AreEqual(selected.TrimEnd(),effective.GetProperty("abc").GetString()!.TrimEnd());
        Assert.IsTrue(effective.GetProperty("lyric_schedule").GetProperty("sections").GetArrayLength()>0);
        Assert.IsFalse(OwnedProcessRegistry.Shared.GetSnapshot().Any(p=>p.Component=="Music.Studio"));
        File.WriteAllText(Path.Combine(output!,"receipt.json"),worker.LastRequestReceipt!);
        File.WriteAllText(Path.Combine(output!,"proof.json"),JsonSerializer.Serialize(new {duration=duration.TotalSeconds,worker.LastHardware,worker.LastRuntimePack,
            score_sha256=MusicScoreVersion.Stamp(selected),studio_released=true,caption="Technical verification; not a music quality rating."}));
    }
}
