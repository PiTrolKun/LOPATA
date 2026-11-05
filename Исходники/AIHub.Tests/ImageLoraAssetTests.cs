using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.ImageLora;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class ImageLoraAssetTests
{
    [TestMethod]
    public async Task FirstUseFastRepeatAuditReinstallMissingAndDeletionShareTheManagedPolicy()
    {
        using var fixture=new MusicProjectTests.Files();
        using var policy=ComponentAudit.UseTestStore(Path.Combine(fixture.Root,"audit"),false);
        var id=Guid.NewGuid().ToString("N");var revision=new string('c',40);
        var header=JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string,object> {
            ["diffusion_model.layers.0.attention.to_k.lora_A.weight"]=new { dtype="F16",shape=new[]{1,3840},data_offsets=new[]{0,7680} },
            ["diffusion_model.layers.0.attention.to_k.lora_B.weight"]=new { dtype="F16",shape=new[]{3840,1},data_offsets=new[]{7680,15360} }
        });
        var bytes=new byte[8+header.Length+15360];BinaryPrimitives.WriteUInt64LittleEndian(bytes,(ulong)header.Length);header.CopyTo(bytes,8);
        var d=new ImageLoraDefinition(id,"Local fixture","Тест","Test","z-image","test/fixture",revision,"apache-2.0","",[],
            [new ManagedModelArtifactFile { RelativePath="adapter.safetensors",SizeBytes=bytes.Length,Sha256=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),SourceUrl="https://huggingface.co/test/fixture/resolve/"+revision+"/adapter.safetensors" }],"lora",true,"Fixture terms","Fixture description");
        var card=d.Card(fixture.Root);var file=Path.Combine(card.InstallDirectory,"adapter.safetensors");Directory.CreateDirectory(card.InstallDirectory);await File.WriteAllBytesAsync(file,bytes);
        var registry=Path.Combine(ImageLoraStore.Root,"Catalog",id+".json");
        var license=Path.Combine(AppDataPaths.BaseDirectory,"Licenses","Extensions",d.LicenseId+".json");
        var entry=Path.Combine(AppDataPaths.ManagedModelLibraryEntriesDirectory,d.LicenseId+".json");
        try {
            ImageLoraStore.Register(d);
            await ImageLoraAssets.PrepareAsync(fixture.Root,[d],true,null,CancellationToken.None);
            Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Root,"audit","receipts")),"Download verification must not consume first-use proof.");
            var use=new ImageLoraUse(d.Id,d.Revision,1);
            using(var lease=await ImageLoraAssets.AcquireAsync(fixture.Root,[use],"z-image",CancellationToken.None)) {
                await Assert.ThrowsAsync<InvalidOperationException>(()=>ImageLoraAssets.RemoveAsync(d,fixture.Root,CancellationToken.None));
            }
            var receipts=Directory.GetFiles(Path.Combine(fixture.Root,"audit","receipts"));Assert.IsGreaterThanOrEqualTo(2,receipts.Length);
            // A repeated audit-off request deliberately checks presence only.
            bytes[^1]=1;await File.WriteAllBytesAsync(file,bytes);
            await ImageLoraAssets.PrepareAsync(fixture.Root,[d],false,null,CancellationToken.None);
            using(ComponentAudit.ForceFull()) await Assert.ThrowsAsync<InvalidDataException>(()=>ImageLoraAssets.PrepareAsync(fixture.Root,[d],false,null,CancellationToken.None));
            bytes[^1]=0;await File.WriteAllBytesAsync(file,bytes);
            await ImageLoraAssets.PrepareAsync(fixture.Root,[d],true,null,CancellationToken.None);
            await ImageLoraAssets.PrepareAsync(fixture.Root,[d],false,null,CancellationToken.None);
            await ImageLoraAssets.RemoveAsync(d,fixture.Root,CancellationToken.None);
            Assert.IsFalse(File.Exists(file));Assert.IsFalse(ImageLoraAssets.Ready(d,fixture.Root));
            await Assert.ThrowsAsync<InvalidDataException>(()=>ImageLoraAssets.PrepareAsync(fixture.Root,[d],false,null,CancellationToken.None));
        } finally {
            // Only this randomly identified fixture's registry entries are removed.
            foreach(var p in new[]{registry,license,entry}) if(File.Exists(p))File.Delete(p);
        }
    }
}
