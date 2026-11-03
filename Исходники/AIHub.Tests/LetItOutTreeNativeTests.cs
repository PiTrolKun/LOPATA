using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass,DoNotParallelize]
public sealed class LetItOutTreeNativeTests
{
    [TestMethod]
    public async Task RealIncrementalMapAndAnAnswerAfterTheServiceRequest()
    {
        var directory=Environment.GetEnvironmentVariable("LOPATA_TREE_NATIVE_DIR");
        if(string.IsNullOrWhiteSpace(directory)){Assert.Inconclusive("Opt-in native tree integration.");return;}
        Directory.CreateDirectory(directory);using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using var audit=ComponentAudit.UseTestStore(Path.Combine(directory,"audit"),false);
        var prepared=await LetItOutChatPreparation.PrepareAsync(new StorageSettingsStore().LoadOrCreate(),2,false,new Progress<LetItOutChatPreparationProgress>(),timeout.Token);
        Assert.IsTrue(prepared.Ready,prepared.StatusKey);
        var store=new LetItOutConversationStore(Path.Combine(directory,"history.db"));var id=Guid.NewGuid().ToString("N");store.Create(id);store.SetTreeEnabled(id,true);
        using var runtime=new LetItOutChatRuntime(new(new UserProfileStore(),new IpLocationService())){MemoryStore=store};
        using var originals=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory,"source-map.json"),timeout.Token));
        var pairs=originals.RootElement.GetProperty("pairs").EnumerateArray().Take(12).ToArray();
        var messages=new List<LetItOutSavedMessage>();var measurements=new List<object>();
        try
        {
            foreach(var batch in pairs.Chunk(3))
            {
                foreach(var pair in batch){messages.Add(new("user",pair.GetProperty("user").GetString()!,true,DateTimeOffset.UtcNow));messages.Add(new("assistant",pair.GetProperty("assistant").GetString()!,true,DateTimeOffset.UtcNow));}
                store.SaveContent(id,messages,"");var timer=System.Diagnostics.Stopwatch.StartNew();
                await runtime.UpdateTreeAsync(new(id,prepared.CoreModelPath!,"ru"),null,timeout.Token);
                var document=store.Tree(id).Document!;Assert.AreEqual(messages.Count/2,document.Through);
                LetItOutTreePolicy.Validate(document.Nodes,document.Through);
                measurements.Add(new{document.Through,document.Revision,nodes=document.Nodes.Length,seconds=timer.Elapsed.TotalSeconds});
                await File.WriteAllTextAsync(Path.Combine(directory,$"map-{document.Through}.json"),JsonSerializer.Serialize(document),timeout.Token);
            }
            var prior=store.Tree(id).Document!;store.SetTreeEnabled(id,false);
            await runtime.UpdateTreeAsync(new(id,prepared.CoreModelPath!,"ru"),null,timeout.Token);
            Assert.AreEqual(prior.Revision,store.Tree(id).Document!.Revision);
            var answer=await runtime.GenerateAsync(new(id,Guid.NewGuid().ToString("N"),prepared.CoreModelPath!,
                messages.Select(m=>new LetItOutChatMessage(m.Role,m.Content)).Append(new("user","Ответь одним предложением: на какую дату в итоге назначена выставка и как она теперь называется?")).ToArray(),"ru"),
                new Progress<ModelStreamChunk>(),timeout.Token);
            Assert.IsFalse(answer.Contains("upsert",StringComparison.OrdinalIgnoreCase));Assert.IsTrue(answer.Contains("29"));
            await File.WriteAllTextAsync(Path.Combine(directory,"result.json"),JsonSerializer.Serialize(new{measurements,answer,sourceMessages=messages.Count}),timeout.Token);
        }
        finally{await runtime.RetireAsync();}
    }
}
