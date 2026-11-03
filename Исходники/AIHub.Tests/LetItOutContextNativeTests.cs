using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class LetItOutContextNativeTests
{
    [TestMethod]
    public async Task InstalledGemmaCountsAndTrimsAnAdditionalTenPercentWithoutChangingTheOriginals()
    {
        var output=Environment.GetEnvironmentVariable("LOPATA_CONTEXT_INDICATOR_NATIVE_DIR");
        if (string.IsNullOrWhiteSpace(output)) { Assert.Inconclusive("Opt-in installed context meter test."); return; }
        Directory.CreateDirectory(output);
        using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(4));
        using var audit=ComponentAudit.UseTestStore(Path.Combine(output,"audit"),false);
        var prepared=await LetItOutChatPreparation.PrepareAsync(new StorageSettingsStore().LoadOrCreate(),2,false,new Inline<LetItOutChatPreparationProgress>(_=>{}),timeout.Token);
        Assert.IsTrue(prepared.Ready,prepared.StatusKey);
        var store=new LetItOutConversationStore(Path.Combine(output,"history.db"));
        var id=Guid.NewGuid().ToString("N"); store.Create(id);
        var paragraph=string.Concat(Enumerable.Repeat("Сегодня мы обсуждали рисунки, выбор материалов и планы на неделю. Я сравнивал бумагу и краски, пробовал разные оттенки и гулял в парке. На следующем занятии хочу продолжить работу над композицией. ",8));
        var messages=Enumerable.Range(1,96).SelectMany(n=>new[]{new LetItOutSavedMessage("user",$"Заметка {n}. "+paragraph),new("assistant","Что из этих проб тебе интересно продолжить?")}).ToArray();
        store.SaveContent(id,messages,"Next draft");
        var request=new LetItOutChatRequest(id,Guid.NewGuid().ToString("N"),prepared.CoreModelPath!,messages.Select(m=>new LetItOutChatMessage(m.Role,m.Content)).Append(new("user","Продолжим про рисунки.")).ToArray(),"ru");
        using var runtime=new LetItOutChatRuntime(new(new UserProfileStore(),new IpLocationService())) { MemoryStore=store,DecideGpu=(_,_)=>Task.FromResult(LlamaGpuDecision.Hybrid) };
        try
        {
            await runtime.WarmAsync(prepared.CoreModelPath!,timeout.Token);
            var before=await runtime.MeasureContextAsync(request,timeout.Token);
            Assert.IsTrue(before.UsedTokens>before.Capacity*.1+2000); Assert.IsTrue(before.CanTrim);
            var after=await runtime.TrimContextAsync(request,timeout.Token);
            Assert.AreEqual(before.Capacity,after.Capacity);
            Assert.IsTrue(before.UsedTokens-after.UsedTokens>=Math.Ceiling(before.Capacity*.10));
            var reopened=new LetItOutConversationStore(store.DatabasePath);
            var repeated=await runtime.MeasureContextAsync(request,timeout.Token);
            Assert.AreEqual(after.UsedTokens,repeated.UsedTokens);
            Assert.IsTrue(reopened.ContextCut(id)>0 && reopened.ContextCut(id)<96);
            var firstCut=reopened.ContextCut(id);
            var second=await runtime.TrimContextAsync(request,timeout.Token);
            Assert.IsTrue(after.UsedTokens-second.UsedTokens>=Math.Ceiling(before.Capacity*.10));
            Assert.IsTrue(reopened.ContextCut(id)>firstCut);
            using var cancelled=new CancellationTokenSource(); cancelled.Cancel();
            var secondCut=reopened.ContextCut(id);
            await Assert.ThrowsAsync<OperationCanceledException>(()=>runtime.TrimContextAsync(request,cancelled.Token));
            Assert.AreEqual(secondCut,reopened.ContextCut(id));
            Assert.HasCount(192,reopened.Load(id)!.Messages); Assert.HasCount(96,reopened.MemoryPairs(id));
            Assert.AreEqual("Next draft",reopened.Load(id)!.Draft);
            Assert.AreEqual(193,request.Messages.Count);
            await File.WriteAllTextAsync(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{before,after,repeated,second,firstCut,through=reopened.ContextCut(id),originalMessages=192}),timeout.Token);
        }
        finally { await runtime.RetireAsync(); }
    }
    private sealed class Inline<T>(Action<T> action):IProgress<T> {public void Report(T value)=>action(value);}
}
