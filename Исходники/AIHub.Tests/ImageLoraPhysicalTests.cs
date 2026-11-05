using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.ImageLora;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class ImageLoraPhysicalTests
{
    [TestMethod, TestCategory("OptIn")]
    public async Task LiveSearchAndCoreDescriptionUseAuthorEvidence()
    {
        if(Environment.GetEnvironmentVariable("LOPATA_IMAGE_LORA_LIVE_SEARCH")!="1") Assert.Inconclusive("Live search and core are opt-in.");
        var root=Environment.GetEnvironmentVariable("LOPATA_IMAGE_LORA_MODELS")!;
        var evidence=Environment.GetEnvironmentVariable("LOPATA_IMAGE_LORA_EVIDENCE")!;
        using var cancel=new CancellationTokenSource(TimeSpan.FromMinutes(5));using var search=new ImageLoraSearch();
        ImageLoraDefinition? found=null;
        await search.InspectAsync("renderartist/Classic-Painting-Z-Image-Turbo-LoRA","z-image",d=>found=d,null!,cancel.Token);
        Assert.IsNotNull(found);
        var summary=await ImageLoraDescriptionService.CreateAsync(found,root,true,_=>{},cancel.Token);
        File.WriteAllText(Path.Combine(evidence,"live-search-summary.json"),JsonSerializer.Serialize(new { found.Repository,found.Revision,summary },new JsonSerializerOptions{WriteIndented=true}));
        Assert.IsNotEmpty(summary.Model,"This check requires a real core summary, not an author fallback.");
        Assert.IsTrue(summary.Text.Any(c=>c is >= 'А' and <= 'я'));
        ImageLoraDefinition? krea=null;
        await search.InspectAsync("krea/Krea-2-LoRA-darkbrush","krea",d=>krea=d,new Progress<string>(_=>{}),cancel.Token);
        Assert.IsNotNull(krea);Assert.Contains("COMMUNITY LICENSE",krea.TermsText);
    }
    [TestMethod, TestCategory("OptIn")]
    public async Task RealAdaptersApplyAndCleanOffPassesRemainAvailable()
    {
        if (Environment.GetEnvironmentVariable("LOPATA_IMAGE_LORA_PHYSICAL") != "1") Assert.Inconclusive("Physical model check is opt-in.");
        var root=Environment.GetEnvironmentVariable("LOPATA_IMAGE_LORA_MODELS") ?? throw new InvalidOperationException("Model root required.");
        var evidence=Environment.GetEnvironmentVariable("LOPATA_IMAGE_LORA_EVIDENCE") ?? throw new InvalidOperationException("Evidence path required.");
        Directory.CreateDirectory(evidence); using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(30));
        using var installation=new ImageGenerationInstallation();
        foreach(var model in new[]{"z-image","krea"}) {
            var adapter=ImageLoraCatalog.For(model).First(d=>d.Supported && (model=="krea"?d.Name=="Darkbrush":d.Repository.StartsWith("renderartist/")));
            var timer=Stopwatch.StartNew();
            await ImageLoraAssets.PrepareAsync(root,[adapter],true,null,deadline.Token);
            File.AppendAllText(Path.Combine(evidence,"progress.log"),model+" adapter ready "+timer.Elapsed+Environment.NewLine);
            var cards=await installation.PrepareAsync(root,model,false,null,deadline.Token);
            var records=new List<object>();var hashes=new List<string>();
            foreach(var mode in new[]{"off-before","on","off-after"}) {
                var session=ImageGenerationSessionStore.Create(evidence);
                var request=new ImageGenerationRequest(Guid.NewGuid().ToString("N"),model,
                    "A red fox reading a book beside a window in a cozy room, detailed illustration",512,512,[20261105],root,session) {
                    EngineSetId="002", Loras=mode=="on"?[new(adapter.Id,adapter.Revision,1)]:[],
                    Metadata=new("LOPATA verification","image-lora-verification") };
                timer.Restart(); var turn=await new ImageGenerationRuntime().RunAsync(request,cards,null,deadline.Token);
                var result=turn.Results.Single(); var path=ImageGenerationSessionStore.ResultPath(request,0);
                var hash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))); hashes.Add(hash);
                Assert.IsTrue(ImageGenerationRuntime.IsValidImage(path,request));
                Assert.AreEqual(mode=="on"?1:0,result.Runtime!.Loras.Length);
                records.Add(new { mode,seconds=timer.Elapsed.TotalSeconds,hash,path,result.Runtime });
                File.WriteAllText(Path.Combine(evidence,model+".json"),JsonSerializer.Serialize(records,new JsonSerializerOptions{WriteIndented=true}));
                File.AppendAllText(Path.Combine(evidence,"progress.log"),model+" "+mode+" "+timer.Elapsed+Environment.NewLine);
            }
            Assert.AreNotEqual(hashes[0],hashes[1],"LoRA must visibly change the generated data.");
        }
    }
}
