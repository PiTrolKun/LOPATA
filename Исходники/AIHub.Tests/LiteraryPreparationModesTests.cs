using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class LiteraryPreparationModesTests
{
    private string _root="";
    private LiteraryChapterStore _chapters=null!;
    private LiteraryProjectLayout Layout=>new(_root);
    private LiteraryJellyStore Memory=>new(Layout);
    private readonly IProgress<LiteraryPreparationProgress> _progress=new Progress<LiteraryPreparationProgress>();
    private static Task<string> Warnings(string _,CancellationToken __)=>Task.FromResult("""{"facts":[{"subject":"","relation":"did","value":"something","kind":"unknown","evidence":"NOT IN SOURCE"}]}""");
    [TestInitialize] public void Setup()
    {
        _root=Path.Combine(Path.GetTempPath(),"lopata-preparation-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root,"project.json"),JsonSerializer.Serialize(new LiteraryProject()));Layout.Initialize();
        _chapters=new(_root);_chapters.Open();_chapters.Save("The library opened in the morning.");_chapters.Finish();_chapters.Save("Active unfinished draft.");
    }
    [TestCleanup] public void Cleanup()=>Directory.Delete(_root,true);
    [TestMethod] public async Task AutomaticModeAcceptsContentWarningsAndDoesNotRepeatReadyParts()
    {
        var service=new LiteraryJellyPreparation(Layout,Warnings);var calls=0;
        var ready=await service.PrepareAsync((_,_)=>{calls++;throw new Exception("Unexpected review");},_progress,default,mode:"auto");
        Assert.IsTrue(ready);Assert.AreEqual(0,calls);Assert.HasCount(1,Memory.Read());
        Assert.IsTrue((await new LiteraryJellyPreparation(Layout,(_,_)=>throw new Exception("Unexpected model call"))
            .PrepareAsync((_,_)=>throw new Exception(),_progress,default,mode:"auto")));
        Assert.IsFalse(LiteraryPreparationPlan.Read(_root).Any(p=>p.Jelly));
        Assert.IsFalse(LiteraryPreparationPlan.Read(_root).Any(p=>p.Id==_chapters.Active.Id));
    }
    [TestMethod] public async Task ManualModeCanAcceptWarningsButNeverChangedSource()
    {
        var service=new LiteraryJellyPreparation(Layout,Warnings);LiteraryJellyBatch? pending=null;
        Assert.IsFalse(await service.PrepareAsync((batch,_)=>{pending=batch;return Task.FromResult(false);},_progress,default,mode:"manual"));
        Assert.HasCount(0,Memory.Read());Assert.IsNotNull(pending);
        Assert.ThrowsExactly<InvalidDataException>(()=>Memory.Confirm(pending,pending.Facts));
        Memory.ConfirmPrepared(pending,pending.Facts,"manual",true);Assert.HasCount(1,Memory.Read());
        var part=_chapters.Index.Parts.First(p=>p.Id==pending.PartId);
        File.WriteAllText(Path.Combine(_root,"chapters",part.FileName),"Changed source");
        Assert.ThrowsExactly<IOException>(()=>Memory.ConfirmPrepared(pending,pending.Facts,"auto"));
    }
    [TestMethod] public async Task TechnicalFailureGetsExactlyThreeRetriesAndOnePrivateReport()
    {
        var calls=0;var service=new LiteraryJellyPreparation(Layout,(_,_)=>{calls++;throw new IOException("PRIVATE SECRET");});
        var result=await LiteraryPreparationRetry.RunAsync(_root,"Jelly",()=>service.PrepareAsync((_,_)=>throw new Exception(),_progress,default,mode:"auto",technicalAttempts:1),default);
        Assert.IsFalse(result.Completed);Assert.AreEqual(4,calls);Assert.IsNotNull(result.Report);
        Assert.HasCount(1,Directory.GetFiles(Path.GetDirectoryName(result.Report)!));
        Assert.IsFalse(File.ReadAllText(result.Report).Contains("PRIVATE SECRET"));Assert.HasCount(0,Memory.Read());
    }
    [TestMethod] public async Task CancellationDoesNotRetryOrCreateFailureReport()
    {
        using var cancel=new CancellationTokenSource();var calls=0;
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(()=>LiteraryPreparationRetry.RunAsync(_root,"RAG",()=>
        {calls++;cancel.Cancel();cancel.Token.ThrowIfCancellationRequested();return Task.FromResult(true);},cancel.Token));
        Assert.AreEqual(1,calls);Assert.IsFalse(Directory.Exists(Path.Combine(_root,"Diagnostics/PreparationReports")));
    }
    [TestMethod] public async Task ReadyRagAndMemoryNeedNoModeDialogAndEmptyArraysAreAccepted()
    {
        var source=LiteraryPreparationPlan.Read(_root).Single();var folder=Layout.EnsureFolder("Rag/Work/"+source.Id);
        File.WriteAllText(Path.Combine(folder,"manifest.json"),JsonSerializer.Serialize(new LiteraryWorkManifest(source.Id,
            LiteraryWorkIndex.Revision(source.Text),new string('a',32),1,GigaEmbeddingInstallation.Revision)));
        var service=new LiteraryJellyPreparation(Layout,(_,_)=>Task.FromResult("[]"));
        Assert.IsTrue(await service.PrepareAsync((_,_)=>throw new Exception(),_progress,default,mode:"auto"));
        Assert.HasCount(0,LiteraryPreparationPlan.Read(_root));
    }
}
