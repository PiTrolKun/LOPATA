using System.Text.Json;
using AIHub.Services;
using AIHub.Services.LiteraryImport;

namespace AIHub.Tests;

public sealed partial class ImportJellyPreparationTests
{
    private ImportPreparationAnswers WorkspaceAnswers()
    {
        var answers = new ImportPreparationAnswers(_session.Root);
        var questions = new ImportPostReviewQuestions(answers);
        while (!questions.AtEnd) questions.Confirm("Accepted " + questions.Current);
        answers.Set("4", "Accepted premise"); answers.Set("post-review.suggestion.15", "UNACCEPTED SECRET");
        return answers;
    }
    private static string Label(string key) => key.EndsWith("NewTitle") ? "Continuation" : key;
    private static Task<int> PublishOffline(string collection, string file, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return Task.FromResult(File.ReadLines(file).Count()); }

    [TestMethod]
    [DataRow("last")]
    [DataRow("new")]
    public async Task WorkspaceActivatesCorrectedBookWithMemoryIndexesAndNoDuplicateEntry(string choice)
    {
        Assert.IsTrue((await Jelly.RunAsync("auto",InvalidContent,Progress,default)).Ready);
        var answers=WorkspaceAnswers(); var before=new LiteraryChapterStore(_project); before.Open();
        var old=before.Index.Parts.ToDictionary(p=>p.FileName,p=>File.ReadAllText(Path.Combine(_project,"chapters",p.FileName)));
        var m=Service.Current()!; var book=Book().Text; var calls=0;
        var service=new ImportWorkspacePreparation(_session,answers,Label,(id,f,t)=>{calls++;return PublishOffline(id,f,t);});
        var result=await service.RunAsync(choice,Quiet,default);
        Assert.IsTrue(result.Ready,result.Report); Assert.AreEqual(m.Parts.Length,calls);
        var chapters=new LiteraryChapterStore(_project); chapters.Open();
        Assert.AreEqual(choice=="new"?m.Parts.Length+1:m.Parts.Length,chapters.Index.Parts.Count);
        Assert.AreEqual(choice=="new"?"":book.Substring(m.Parts[^1].Start,m.Parts[^1].Length),chapters.Load());
        Assert.IsFalse(chapters.Active.Finished); Assert.AreEqual("workspace-ready",_session.State.Stage);
        var state=service.Load()!; Assert.IsTrue(state.Ready);
        foreach(var (name,text) in old) Assert.AreEqual(text,File.ReadAllText(Path.Combine(_project,"Import/BeforeWorkspace",state.Activation,name)));
        var snapshot=LiteraryEditorSnapshot.Capture(m.ProjectId,_project,chapters.Index,chapters.Load(),false);
        foreach(var part in m.Parts)
        {
            var source=snapshot.Sources.Single(s=>s.Id==part.Id); var text=File.ReadAllText(Path.Combine(_project,"chapters",source.FileName));
            Assert.AreEqual(book.Substring(part.Start,part.Length),text);
            Assert.IsNotNull(LiteraryWorkIndex.Current(new(_project),source,text));
            var vectorFile=Path.Combine(_project,"Rag/Work",part.Id,"import-vectors.jsonl");
            foreach(var line in File.ReadLines(vectorFile))
            {
                using var doc=JsonDocument.Parse(line);var payload=doc.RootElement.GetProperty("payload");
                Assert.AreEqual(part.Id,payload.GetProperty("source").GetString());
                var offset=payload.GetProperty("offset").GetInt32(); var chars=string.Concat(text.EnumerateRunes().Skip(offset).Select(r=>r.ToString()));
                Assert.IsTrue(chars.StartsWith(payload.GetProperty("text").GetString()!,StringComparison.Ordinal));
            }
        }
        var context=new LiteraryJellyContext(new(_project),snapshot,"Person",(_,_)=>{}).Build(20000);
        using(var json=JsonDocument.Parse(context))
        {
            var expected=Memory.Read().Count(e=>e.PartId!=snapshot.ActiveId);
            Assert.AreEqual(expected,json.RootElement.GetProperty("facts").GetArrayLength()+json.RootElement.GetProperty("omitted").GetInt32());
        }
        var project=LiteraryProjectStore.ReadProject(_project);
        Assert.AreEqual("Accepted premise",project.Premise); Assert.IsFalse(project.CreationBrief.Contains("UNACCEPTED SECRET"));
        var active=chapters.Active.Id; chapters.Save("Edited after opening");
        Assert.IsTrue((await service.RunAsync(choice=="new"?"last":"new",Quiet,default)).Ready);
        chapters.Open(); Assert.AreEqual(active,chapters.Active.Id); Assert.AreEqual("Edited after opening",chapters.Load());
        Assert.AreEqual(m.Parts.Length,calls); Assert.AreEqual(book,Book().Text);
    }

    [TestMethod] public async Task WorkspaceRetryPreservesChoiceAndOldChapters()
    {
        await Jelly.RunAsync("auto",Empty,Progress,default); var answers=WorkspaceAnswers();
        var index=File.ReadAllText(Path.Combine(_project,"chapters/index.json")); var calls=0;
        var failed=new ImportWorkspacePreparation(_session,answers,Label,(_,_,_)=>{calls++;throw new IOException("PRIVATE SOURCE");});
        var result=await failed.RunAsync("new",Quiet,default);
        Assert.IsFalse(result.Ready);Assert.AreEqual(4,calls);Assert.HasCount(1,Directory.GetFiles(Path.GetDirectoryName(result.Report!)!));
        Assert.IsFalse(File.ReadAllText(result.Report!).Contains("PRIVATE SOURCE"));
        Assert.AreEqual(index,File.ReadAllText(Path.Combine(_project,"chapters/index.json")));
        var resumed=new ImportWorkspacePreparation(_session,answers,Label,PublishOffline);
        Assert.IsTrue((await resumed.RunAsync("last",Quiet,default)).Ready);
        Assert.AreEqual("new",resumed.Load()!.Choice);var chapters=new LiteraryChapterStore(_project);chapters.Open();Assert.AreEqual("",chapters.Load());
    }

    [TestMethod] public async Task WorkspaceCancellationReusesPublishedPartIndexes()
    {
        await Jelly.RunAsync("auto",Empty,Progress,default);var answers=WorkspaceAnswers();using var cancel=new CancellationTokenSource();var calls=0;
        var service=new ImportWorkspacePreparation(_session,answers,Label,(id,f,t)=>
        {calls++;cancel.Cancel();return Task.FromResult(File.ReadLines(f).Count());});
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(()=>service.RunAsync("last",Quiet,cancel.Token));
        var after=0; var resumed=new ImportWorkspacePreparation(_session,answers,Label,(id,f,t)=>{after++;return PublishOffline(id,f,t);});
        Assert.IsTrue((await resumed.RunAsync("last",Quiet,default)).Ready);
        Assert.AreEqual(Service.Current()!.Parts.Length,calls+after);
    }

    [TestMethod] public async Task WorkspaceResumesAfterChapterCommitBeforeProjectCommit()
    {
        await Jelly.RunAsync("auto",Empty,Progress,default);var answers=WorkspaceAnswers();
        var book=Book().Text;FileStream? locked=null;
        var service=new ImportWorkspacePreparation(_session,answers,Label,(id,f,t)=>
        {
            locked ??= new FileStream(Path.Combine(_project,"project.json"),FileMode.Open,FileAccess.Read,FileShare.Read);
            return PublishOffline(id,f,t);
        });
        try { Assert.IsFalse((await service.RunAsync("new",Quiet,default)).Ready); }
        finally { locked?.Dispose(); }
        var pending=service.Load()!;Assert.IsFalse(pending.Ready);
        var chapters=new LiteraryChapterStore(_project);chapters.Open();
        Assert.AreEqual(pending.Activation,chapters.Index.Transaction);
        var active=chapters.Active.Id;
        Assert.IsTrue((await service.RunAsync("last",Quiet,default)).Ready);
        chapters.Open();Assert.AreEqual(active,chapters.Active.Id);Assert.AreEqual("",chapters.Load());
        Assert.AreEqual(book,Book().Text);
    }

    [TestMethod] public async Task WorkspaceRefusesChangedBookAndUnfinishedMemory()
    {
        var answers=WorkspaceAnswers();var service=new ImportWorkspacePreparation(_session,answers,Label,(_,_,_)=>throw new Exception("Must not publish"));
        Assert.IsFalse((await service.RunAsync("new",Quiet,default)).Ready);
        await Jelly.RunAsync("auto",Empty,Progress,default);SetBook(Book().Text+" changed");
        Assert.IsFalse((await service.RunAsync("new",Quiet,default)).Ready);Assert.IsNull(service.Load());
    }

    [TestMethod] public async Task ActivationRollsBackOverwrittenNamesWhenIndexCannotCommit()
    {
        var store=new LiteraryChapterStore(_project);store.Open();var original=store.Index;
        var old=original.Parts.ToDictionary(p=>p.FileName,p=>File.ReadAllText(Path.Combine(_project,"chapters",p.FileName)));
        var hashes=old.Keys.ToDictionary(n=>n,n=>ImportSession.HashFile(Path.Combine(_project,"chapters",n)));
        var next=JsonSerializer.Deserialize<LiteraryChapterIndex>(JsonSerializer.Serialize(original))!;
        next.Parts[0].Id=Guid.NewGuid().ToString("N");var texts=old.ToDictionary(p=>p.Key,p=>"Corrected "+p.Value);
        using(var locked=new FileStream(Path.Combine(_project,"chapters/index.json"),FileMode.Open,FileAccess.Read,FileShare.Read))
            Assert.ThrowsExactly<IOException>(()=>store.ActivateImport(next,texts,original.Transaction,hashes,Guid.NewGuid().ToString("N")));
        store.Open();foreach(var (name,text) in old) Assert.AreEqual(text,File.ReadAllText(Path.Combine(_project,"chapters",name)));
        Assert.AreEqual(original.Transaction,store.Index.Transaction);
        await Task.CompletedTask;
    }
}
