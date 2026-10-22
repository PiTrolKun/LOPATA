using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.MusicAceIdeas;

namespace AIHub.Tests;

[TestClass,DoNotParallelize]
public sealed class MusicAceIdeaPhysicalTests
{
    [TestMethod]
    public async Task InstalledAcePlansWithoutDitThenRendersOriginalAndRetakeFromTheSameCodes() {
        var models=Environment.GetEnvironmentVariable("LOPATA_ACE_IDEA_LIVE_MODELS");var output=Environment.GetEnvironmentVariable("LOPATA_ACE_IDEA_LIVE_OUTPUT");
        if(string.IsNullOrWhiteSpace(models)||string.IsNullOrWhiteSpace(output))Assert.Inconclusive("Explicit opt-in with installed models and a fresh output folder is required.");
        if(Directory.Exists(output))throw new IOException("Use a new proof directory");Directory.CreateDirectory(output!);
        using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(8));var expert=MusicAceCatalog.Defaults();expert.Values["bpm"]=120;
        expert.Values["inference_steps"]=8;var input=new MusicProjectSnapshot{Model=MusicAceCatalog.ModelName,Variation=MusicAceCatalog.Variation,
            ModelRevision=MusicAceCatalog.ModelRevision,Expert=expert,DurationSeconds=10,Lyrics="[Instrumental]",Wishes=MusicWishSnapshot.Capture(new(){Instrumental=true})};
        const string style="Light acoustic guitar instrumental, gentle rhythm, 120 BPM.";
        var request=new MusicYueRequest(style,input.Lyrics,12347,12347,10){Expert=expert,Wishes=input.Wishes};
        var worker=new MusicAceWorker();worker.Log+=line=>File.AppendAllText(Path.Combine(output!,"worker.log"),line+"\n");
        var model=MusicModelVariants.Artifact(models!,MusicAceCatalog.Variation,MusicAceCatalog.Variation);
        var decoder=MusicModelVariants.Artifact(models!,MusicAceCatalog.Variation,MusicAceCatalog.Companions);
        var plan=Path.Combine(output!,"idea.json");await worker.PlanAsync(model,request,Path.Combine(output!,"plan-request.json"),plan,deadline.Token);
        using(var receipt=JsonDocument.Parse(worker.LastRequestReceipt!))Assert.IsTrue(receipt.RootElement.GetProperty("lm_only").GetBoolean());
        File.WriteAllText(Path.Combine(output!,"plan-receipt.json"),worker.LastRequestReceipt);
        Assert.IsFalse(OwnedProcessRegistry.Shared.GetSnapshot().Any(p=>p.Component=="Music.ACE.XL"));
        var data=JsonSerializer.Deserialize<MusicAceIdeaData>(await File.ReadAllTextAsync(plan,deadline.Token))!;data.Validate();
        var idea=new MusicAceIdea{Name="physical",Seed=12347,BaseSoundSeed=23458,Input=input,Style=style,Data=data,Hash=MusicAceIdea.Stamp(data.Codes),
            InputHash=MusicAceIdea.Bind(style,input.Lyrics,expert,[],10)};idea.Validate();
        foreach(var take in new[]{new MusicAceIdeaUse(idea),new MusicAceIdeaUse(idea,34569,.2)}) {
            var name=take.RetakeSeed is null?"original":"retake";var audio=Path.Combine(output!,name+".wav");
            await worker.SynthesizeAsync(model,decoder,request with {SoundSeed=23458,AceIdea=take},Path.Combine(output!,name+"-request.json"),audio,deadline.Token);
            MusicAceIdeaPipeline.Confirm(take,worker.LastRequestReceipt,23458);Assert.IsTrue(MusicWaveFile.ReadDuration(audio).TotalSeconds>0);
            File.WriteAllText(Path.Combine(output!,name+"-receipt.json"),worker.LastRequestReceipt);
            Assert.IsFalse(OwnedProcessRegistry.Shared.GetSnapshot().Any(p=>p.Component=="Music.ACE.XL"));
        }
        using(var original=File.OpenRead(Path.Combine(output!,"original.wav")))
        using(var retake=File.OpenRead(Path.Combine(output!,"retake.wav")))
            Assert.AreNotEqual(Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(original,deadline.Token)),
                Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(retake,deadline.Token)),"Retake must not silently reproduce identical PCM.");
        File.WriteAllText(Path.Combine(output!,"proof.json"),JsonSerializer.Serialize(new {idea.Hash,worker.LastHardware,worker.LastRuntimePack,
            baselineSeed=23458,retakeSeed=34569,variance=.2,lm_only=true,owned_processes_released=true,quality="not rated"}));
    }
    [TestMethod]
    public async Task RetakeChangesActualNoiseAndAudioUsingAnAlreadyVerifiedIdea() {
        var models=Environment.GetEnvironmentVariable("LOPATA_ACE_IDEA_LIVE_MODELS");var originalProof=Environment.GetEnvironmentVariable("LOPATA_ACE_IDEA_RETAKE_SOURCE");
        var output=Environment.GetEnvironmentVariable("LOPATA_ACE_IDEA_LIVE_OUTPUT");
        if(string.IsNullOrWhiteSpace(models)||string.IsNullOrWhiteSpace(originalProof)||string.IsNullOrWhiteSpace(output))Assert.Inconclusive("Explicit targeted retake opt-in required");
        if(Directory.Exists(output))throw new IOException("Use a fresh proof directory");Directory.CreateDirectory(output!);
        using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(5));var expert=MusicAceCatalog.Defaults();expert.Values["bpm"]=120;
        var input=new MusicProjectSnapshot{Model=MusicAceCatalog.ModelName,Variation=MusicAceCatalog.Variation,ModelRevision=MusicAceCatalog.ModelRevision,
            Expert=expert,DurationSeconds=10,Lyrics="[Instrumental]",Wishes=MusicWishSnapshot.Capture(new(){Instrumental=true})};
        const string style="Light acoustic guitar instrumental, gentle rhythm, 120 BPM.";
        var data=JsonSerializer.Deserialize<MusicAceIdeaData>(File.ReadAllText(Path.Combine(originalProof!,"idea.json")))!;data.Validate();
        var idea=new MusicAceIdea{Name="targeted",Seed=12347,BaseSoundSeed=23458,Input=input,Style=style,Data=data,Hash=MusicAceIdea.Stamp(data.Codes),
            InputHash=MusicAceIdea.Bind(style,input.Lyrics,expert,[],10)};
        var use=new MusicAceIdeaUse(idea,34569,.2);var worker=new MusicAceWorker();worker.Log+=line=>File.AppendAllText(Path.Combine(output!,"worker.log"),line+"\n");
        var request=new MusicYueRequest(style,input.Lyrics,23458,23458,10){Expert=expert,Wishes=input.Wishes,AceIdea=use};
        var audio=Path.Combine(output!,"retake.wav");
        await worker.SynthesizeAsync(MusicModelVariants.Artifact(models!,MusicAceCatalog.Variation,MusicAceCatalog.Variation),
            MusicModelVariants.Artifact(models!,MusicAceCatalog.Variation,MusicAceCatalog.Companions),request,Path.Combine(output!,"request.json"),audio,deadline.Token);
        MusicAceIdeaPipeline.Confirm(use,worker.LastRequestReceipt,23458);File.WriteAllText(Path.Combine(output!,"receipt.json"),worker.LastRequestReceipt);
        using var receipt=JsonDocument.Parse(worker.LastRequestReceipt!);var adapter=receipt.RootElement.GetProperty("retake_adapter");
        Assert.IsTrue(adapter.GetProperty("applied").GetBoolean());Assert.AreEqual(1,adapter.GetProperty("noise_calls").GetInt32());
        Assert.AreNotEqual(adapter.GetProperty("base_noise_sha256").GetString(),adapter.GetProperty("mixed_noise_sha256").GetString());
        using var baseline=File.OpenRead(Path.Combine(originalProof!,"original.wav"));using var retake=File.OpenRead(audio);
        var originalHash=Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(baseline,deadline.Token));
        var retakeHash=Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(retake,deadline.Token));
        Assert.AreNotEqual(originalHash,retakeHash);Assert.IsFalse(OwnedProcessRegistry.Shared.GetSnapshot().Any(p=>p.Component=="Music.ACE.XL"));
        File.WriteAllText(Path.Combine(output!,"proof.json"),JsonSerializer.Serialize(new{idea.Hash,originalHash,retakeHash,adapter,
            duration=MusicWaveFile.ReadDuration(audio).TotalSeconds,worker.LastHardware,worker.LastRuntimePack,quality="not rated"}));
    }
}
