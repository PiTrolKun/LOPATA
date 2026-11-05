using System.Diagnostics;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.ImageLora;
using AIHub.Controls;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class ImageLoraTests
{
    [TestMethod]
    public void FullCatalogRetainsFutureAndUnknownBasesButOnlyCompatibleAdaptersAreSelectable()
    {
        Assert.HasCount(27, ImageLoraCatalog.Bundled);
        Assert.HasCount(22, ImageLoraCatalog.Bundled.Select(d=>d.Repository).Distinct().ToArray());
        Assert.IsTrue(ImageLoraCatalog.Bundled.Any(d=>d.ModelId=="krea-raw" && !d.Supported));
        Assert.IsTrue(ImageLoraCatalog.Bundled.Any(d=>d.Kind=="checkpoint" && !d.Supported));
        foreach(var model in new[]{"z-image","krea"}) {
            var available=ImageLoraCatalog.For(model);
            Assert.IsTrue(available.Any(d=>d.Supported)); Assert.IsTrue(available.All(d=>d.ModelId==model));
            Assert.IsFalse(available.Any(d=>d.Kind=="checkpoint"));
        }
        Assert.IsFalse(ImageLoraCatalog.For("sdxl").Any());
    }
    [TestMethod]
    public void MasterSwitchPreservesChoicesAndModelSwitchDoesNotLeakAdapters()
    {
        var z=ImageLoraCatalog.For("z-image").First(d=>d.Supported);
        var selection=new ImageLoraSelection { ByModel=new() { ["z-image"]=[new(z.Id,.75)] } };
        Assert.HasCount(1,ImageLoraCatalog.Capture(selection,"z-image"));
        Assert.IsEmpty(ImageLoraCatalog.Capture(selection,"krea"));
        Assert.IsEmpty(ImageLoraCatalog.Capture(selection with { Enabled=false },"z-image"));
        Assert.AreEqual(.75,selection.ByModel["z-image"][0].Strength);
        var snapshot=selection.Snapshot(); selection.ByModel.Clear(); Assert.HasCount(1,snapshot.ByModel);
    }
    [TestMethod]
    public void InvalidRevisionsWeightsAndForeignBasesAreRejected()
    {
        var z=ImageLoraCatalog.For("z-image").First(d=>d.Supported);
        var use=new ImageLoraUse(z.Id,z.Revision,.8);
        Assert.Throws<InvalidDataException>(()=>ImageLoraCatalog.ValidateUses([use],"krea"));
        Assert.Throws<InvalidDataException>(()=>ImageLoraCatalog.ValidateUses([use with { Revision=new('0',40) }],"z-image"));
        Assert.Throws<InvalidDataException>(()=>ImageLoraCatalog.ValidateUses([use with { Strength=double.NaN }],"z-image"));
        Assert.Throws<InvalidDataException>(()=>ImageLoraCatalog.ValidateUses([use,use],"z-image"));
    }
    [TestMethod]
    public void EffectivePromptKeepsUserTextAddsTriggerOnceAndUsesContainedRelativePaths()
    {
        var z=ImageLoraCatalog.For("z-image").First(d=>d.Repository.StartsWith("renderartist/"));
        var use=new ImageLoraUse(z.Id,z.Revision,.75); var raw="  Кот у окна\nс книгой  ";
        var prompt=ImageLoraCatalog.EffectivePrompt(raw,[use]);
        Assert.Contains(raw,prompt); Assert.Contains("class1cpa1nt",prompt);
        Assert.Contains("<lora:"+z.Id+"/"+z.Revision[..12]+"/adapter:0.75>",prompt);
        Assert.AreEqual(raw,ImageLoraCatalog.EffectivePrompt(raw,[]));
        var once=ImageLoraCatalog.EffectivePrompt("class1cpa1nt cat",[use]);
        Assert.AreEqual(1,once.Split("class1cpa1nt").Length-1);
    }
    [TestMethod]
    public void NativeCommandAndMetadataUseCapturedSelectionNotCurrentSettings()
    {
        var d=ImageLoraCatalog.For("krea").First(d=>d.Supported);
        var request=new ImageGenerationRequest("test","krea","cat",256,256,[42],"D:\\models","D:\\session") { Loras=[new(d.Id,d.Revision,.8)] };
        var components=ImageGenerationCatalog.Get(request.ModelId).Components;
        var cards=ImageGenerationCatalog.CreateCards(request.ModelsRoot).Where(c=>components.Contains(c.ModelArtifactId)).ToArray();
        var command=ImageGenerationNativeWorker.Command(request,0,cards,"effective.txt","output.png","sd-cli.exe");
        var args=command.ArgumentList.ToArray();
        Assert.AreEqual("at_runtime",args[Array.IndexOf(args,"--lora-apply-mode")+1]);
        Assert.AreEqual(System.IO.Path.Combine(request.ModelsRoot,"ImageLoRA"),args[Array.IndexOf(args,"--lora-model-dir")+1]);
        Assert.IsFalse(command.UseShellExecute);
        var receipt=new ImageGenerationRuntimeReceipt("002","test","revision","vulkan","runtime") {
            Loras=[new(d.Id,d.Repository,d.Revision,d.Files[0].Sha256,.8,d.Trigger)] };
        using var json=JsonDocument.Parse(ImageGenerationMetadata.Create(request,0,DateTimeOffset.UtcNow,receipt)["LOPATA"]);
        Assert.AreEqual(d.Revision,json.RootElement.GetProperty("AppliedLoras")[0].GetProperty("Revision").GetString());
        Assert.AreEqual("cat",json.RootElement.GetProperty("Prompt").GetString());
        using var legacy=JsonDocument.Parse(ImageGenerationMetadata.Create(request,0,DateTimeOffset.UtcNow)["LOPATA"]);
        Assert.AreEqual(0,legacy.RootElement.GetProperty("AppliedLoras").GetArrayLength());
    }
    [TestMethod]
    public void HeadersMustMatchTheExactArchitectureAndHaveCompletePairs()
    {
        var a="diffusion_model.layers.0.attention.to_k.lora_A.weight";
        var b="diffusion_model.layers.0.attention.to_k.lora_B.weight";
        JsonElement Header(int input=3840,bool pair=true) => JsonSerializer.SerializeToElement(new Dictionary<string,object> {
            [a]=new { dtype="F16",shape=new[]{16,input},data_offsets=new[]{0,16*input*2} },
            [pair?b:"wrong.name"]=new { dtype="F16",shape=new[]{3840,16},data_offsets=new[]{16*input*2,16*input*2+3840*16*2} } });
        var good=Header(); ImageLoraTensorRules.ValidateStorage(good,16*3840*4); ImageLoraTensorRules.Validate(good,"z-image");
        Assert.Throws<InvalidDataException>(()=>ImageLoraTensorRules.Validate(good,"krea"));
        Assert.Throws<InvalidDataException>(()=>ImageLoraTensorRules.Validate(Header(4096),"z-image"));
        Assert.Throws<InvalidDataException>(()=>ImageLoraTensorRules.Validate(Header(pair:false),"z-image"));
    }
    [TestMethod]
    public void RussianTopicsExpandAndBaseMatchingDoesNotConfuseRawAndTurbo()
    {
        Assert.Contains("anatomy",ImageLoraSearch.Terms("Улучшение отрисовки тела"));
        Assert.Contains("uncensored",ImageLoraSearch.Terms("Меньше цензуры"));
        var raw=JsonSerializer.SerializeToElement(new { cardData=new { base_model="krea/Krea-2-Raw" } });
        var turbo=JsonSerializer.SerializeToElement(new { cardData=new { base_model=new[]{"krea/Krea-2-Turbo"} } });
        Assert.IsFalse(ImageLoraSearch.ExactBase(raw,"krea")); Assert.IsTrue(ImageLoraSearch.ExactBase(turbo,"krea"));
        var facts=ImageLoraDescriptionService.DescriptionFacts("---\nwidget:\n- text: a woman on a chair\n---\n## Model description\nPaint textures, not artist identities.");
        Assert.DoesNotContain("woman",facts);Assert.Contains("Paint textures",facts);
    }
    [TestMethod]
    public void EngineWarningsCannotSilentlyPublishAnUnmodifiedImage()
    {
        Assert.IsTrue(ImageGenerationNativeWorker.LoraRejected("unused lora tensor |abc|"));
        Assert.IsTrue(ImageGenerationNativeWorker.LoraRejected("failed to load LoRA"));
        Assert.IsTrue(ImageGenerationNativeWorker.LoraRejected("skipped incompatible lora tensor"));
        Assert.IsFalse(ImageGenerationNativeWorker.LoraRejected("loaded lora model successfully"));
        var d=ImageLoraCatalog.For("z-image").First(d=>d.Supported);var use=new ImageLoraUse(d.Id,d.Revision,1);
        var log="[INFO] (480 / 480) LoRA tensors have been applied, lora_file_path = H:/models/ImageLoRA/"+d.Id+"/"+d.Revision[..12]+"/adapter.safetensors";
        Assert.IsTrue(ImageGenerationNativeWorker.LorasApplied(log,[use]));
        Assert.IsFalse(ImageGenerationNativeWorker.LorasApplied(log.Replace("480 / 480","478 / 480"),[use]));
        Assert.IsFalse(ImageGenerationNativeWorker.LorasApplied("loaded lora model successfully",[use]));
        Assert.Throws<InvalidDataException>(()=>ImageLoraCatalog.ValidateRawPrompt("cat <lora:outside:1>"));
    }
    [TestMethod]
    public void WindowStartsWithBuiltinsAndAllSixThemesWithoutNetworkOrCore()
    {
        Exception? failure=null;
        var thread=new Thread(()=>{
            try {
                var window=new ImageLoraWindow(null,"D:\\models","z-image",new(),key=>key,true);
                Assert.AreEqual("Generation.Lora.Window",AutomationProperties.GetAutomationId(window));
                IEnumerable<DependencyObject> Walk(DependencyObject root) {
                    yield return root;
                    foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) foreach(var n in Walk(child))yield return n;
                }
                var ids=Walk(window).OfType<FrameworkElement>().Select(AutomationProperties.GetAutomationId).ToArray();
                Assert.Contains("Generation.Lora.Topic.Body",ids);Assert.Contains("Generation.Lora.Topic.Uncensored",ids);
                Assert.IsTrue(ids.Any(x=>x.StartsWith("Generation.Lora.Select.")));
                window.Close();
            } catch(Exception e) { failure=e; }
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(20)));
        if(failure is not null)throw failure;
    }
}
