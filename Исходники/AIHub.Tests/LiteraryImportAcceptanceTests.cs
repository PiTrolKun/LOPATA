using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.LiteraryImport;

namespace AIHub.Tests;

[TestClass]
public sealed class LiteraryImportAcceptanceTests
{
    private string _root="";
    [TestInitialize] public void Setup() { _root=Path.Combine(Path.GetTempPath(),"lopata-acceptance-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root); }
    [TestCleanup] public void Cleanup() { if(Path.GetFullPath(_root).StartsWith(Path.Combine(Path.GetTempPath(),"lopata-acceptance-"),StringComparison.OrdinalIgnoreCase)) Directory.Delete(_root,true); }
    private ImportSession Session()
    { var p=Path.Combine(_root,"input.json"); File.WriteAllText(p,"[]"); var s=ImportSession.Create(_root,p,default); s.State.SelectedProject="Synthetic"; return s; }
    private (LiteraryProjectEntry Entry,LiteraryChapterStore Chapters) Project(ImportSession session,params (string Text,string Kind)[] rows)
    {
        var units=rows.Select((r,i)=>new ImportUnit("u"+i,"c","m"+i,"","RESPONSE",0,0,r.Text,false)).ToArray();
        var decisions=rows.Select((r,i)=>new ImportDecision("u"+i,r.Kind,"Synthetic","Part "+i,"test decision")).ToArray();
        var e=ImportProjectBuilder.Build(session,new([new("c","synthetic",rows.Length)],units.ToList(),[],[]),decisions,
            new(Path.Combine(_root,"projects.json")),_root,"Book","test","ru");
        var chapters=new LiteraryChapterStore(e.ProjectPath); chapters.Open(); return(e,chapters);
    }
    [TestMethod] public async Task EmptyReferenceAndFullyFlaggedRagArePartialNotFatal()
    {
        using var session=Session(); var (e,chapters)=Project(session,("Chat passage","DOUBT"));
        var project=LiteraryProjectStore.ReadProject(e.ProjectPath);
        var snapshot=LiteraryEditorSnapshot.Capture(e.Id,e.ProjectPath,chapters.Index,"",false);
        var evidence=await new LiteraryParagraphSources(project,snapshot,new(project,snapshot,x=>x)).ReadAsync("Continue",
            new Dictionary<string,ParagraphSelection>(){["rag"]=new(){Selected=true},["jelly"]=new(){Selected=true}},_=>{},default);
        Assert.IsTrue(evidence.Complete); Assert.AreEqual("partial",evidence.Receipts.Single(r=>r.Id=="rag").Status);
        Assert.AreEqual("empty",evidence.Receipts.Single(r=>r.Id=="jelly").Status);
        Assert.IsTrue(evidence.Materials.Any(m=>m.Kind=="source_coverage"));
    }
    [TestMethod] public async Task MissingRequiredIndexStillFailsAfterSkippingFlaggedPart()
    {
        using var session=Session(); var(e,chapters)=Project(session,("Chat passage","DOUBT"),("Book passage","MAIN"));
        var snapshot=LiteraryEditorSnapshot.Capture(e.Id,e.ProjectPath,chapters.Index,"",false);
        var ex=await Assert.ThrowsAsync<InvalidDataException>(()=>new LiteraryRagReader(snapshot).SearchScopedAsync("Continue",false,"",false,default));
        StringAssert.Contains(ex.Message,"002");
    }
    [TestMethod] public async Task AttachedReferenceWithMissingIndexIsNotTreatedAsEmpty()
    {
        using var session=Session(); var(e,chapters)=Project(session,("Chat passage","DOUBT"));
        var folder=Path.Combine(e.ProjectPath,"Rag","Source"); Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder,"sources.json"),"[]");
        var snapshot=LiteraryEditorSnapshot.Capture(e.Id,e.ProjectPath,chapters.Index,"",false);
        StringAssert.Contains(await new LiteraryRagReader(snapshot).SearchScopedAsync("Continue",true,"",false,default),"Reference index is not ready");
    }
    [TestMethod] public void EditingReviewPreservesOriginalAndRejectsStaleAndOversizedSave()
    {
        using var session=Session(); var(e,chapters)=Project(session,("Chat. Book.","DOUBT")); var p=chapters.Index.Parts.First();
        Assert.Throws<IOException>(()=>ImportReviewEdits.Apply(e.ProjectPath,p.Id,"wrong","Book."));
        Assert.Throws<InvalidDataException>(()=>ImportReviewEdits.Apply(e.ProjectPath,p.Id,"Chat. Book.",new string('x',7501)));
        ImportReviewEdits.Apply(e.ProjectPath,p.Id,"Chat. Book.","Book.");
        Assert.HasCount(0,ImportEligibility.Review(e.ProjectPath,p.Id)!.Doubts);
        Assert.AreEqual("Book.",File.ReadAllText(Path.Combine(e.ProjectPath,"chapters",p.FileName)));
        var audit=File.ReadAllText(Directory.GetFiles(Path.Combine(e.ProjectPath,"Import"),"review-*.json").Single());
        StringAssert.Contains(audit,"Chat. Book."); StringAssert.Contains(audit,"user-edited-and-reviewed-part");
        ImportReviewEdits.Apply(e.ProjectPath,p.Id,"Book.","",exclude:true);
        Assert.HasCount(0,chapters.Snapshot());
        var docx=Path.Combine(_root,"excluded.docx"); ImportBookExporter.Export(e.ProjectPath,docx,true,default);
        using var doc=DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(docx,false);
        Assert.IsFalse(doc.MainDocumentPart!.Document!.InnerText.Contains("Book."));
    }
    [TestMethod] public void EmptyMemoryBatchesCompleteWithoutInventingFacts()
    {
        using var session=Session(); var(e,chapters)=Project(session,("Book.","MAIN")); var p=chapters.Index.Parts.First();
        var memory=new LiteraryJellyStore(new(e.ProjectPath)); var batch=new LiteraryJellyBatch(Guid.NewGuid().ToString("N"),p.Id,"001",LiteraryWorkIndex.Revision("Book."),"Book.",[]);
        memory.Stage(batch); memory.ConfirmEmpty(batch);
        Assert.AreEqual("confirmed",memory.Find(p.Id,batch.Revision)!.Status); Assert.HasCount(0,memory.Read());
        StringAssert.Contains(JsonSerializer.Serialize(memory.ExportAudit()),"empty_batch_completed");
    }
    [TestMethod] public void ExportContainsLaterMemoryDecisionsAndUpdatesStatus()
    {
        using var session=Session(); var(e,chapters)=Project(session,("Hero arrived.","MAIN")); var p=chapters.Index.Parts.First();
        var memory=new LiteraryJellyStore(new(e.ProjectPath)); var fact=new LiteraryJellyFact { Subject="Hero",Relation="arrived",Evidence="Hero arrived." };
        var batch=new LiteraryJellyBatch(Guid.NewGuid().ToString("N"),p.Id,"001",LiteraryWorkIndex.Revision("Hero arrived."),"Hero arrived.",[fact]);
        memory.Stage(batch); ImportCompletion.Export(session,e,"ru",default);
        Assert.AreEqual(1,ImportProjectStatus.Read(e.ProjectPath).Pending);
        memory.Confirm(batch,[fact]); ImportCompletion.Export(session,e,"ru",default);
        Assert.AreEqual(1,ImportProjectStatus.Read(e.ProjectPath).Confirmed);
        var snapshots=session.State.Artifacts.Where(a=>a.Step.StartsWith("jelly-review-snapshot/")).ToArray(); Assert.HasCount(2,snapshots);
        StringAssert.Contains(File.ReadAllText(session.ArtifactPath(snapshots[^1])),"user_approved");
        ImportBlackBox.Verify(Path.Combine(e.ProjectPath,"Exports/Import/import-history.xlsx"),session.State.Artifacts,default);
    }
    [TestMethod] public void SimilarNamesOnlySuggestAndExplicitMergePreservesEveryUnit()
    {
        Assert.IsTrue(ImportWorkNames.Similar("Легенда о Синем и Дальнем Море","Легенда о Синим и Дальнем Море"));
        Assert.IsFalse(ImportWorkNames.Similar("Легенда о море 1","Легенда о море 2"));
        Assert.IsFalse(ImportWorkNames.Similar("Любовь","Морковь"));
        var first=new[]{new ImportDecision("a","KEEP","One","chapter",""),new ImportDecision("b","KEEP","One typo","chapter",""),new ImportDecision("c","KEEP","Other book","chapter","")};
        var merged=ImportWorkNames.Merge(first,["One","One typo"],"One");
        Assert.AreEqual("One",merged[1].Project); Assert.AreEqual("Other book",merged[2].Project);
        CollectionAssert.AreEqual(first,merged.Select((d,i)=>d with { Project=first[i].Project }).ToArray());
    }
    [TestMethod] public async Task ReassemblyReusesFirstPassAndLeavesOriginalProjectIdentityUntouched()
    {
        using var session=Session(); session.State.Conversations=["c"]; var(e,_)=Project(session,("Book.","MAIN"));
        var input=new ImportInput([new("c","synthetic",1)],[new("u","c","m","","RESPONSE",0,0,"Book.",false)],[],[]);
        var pipe=new ImportPipeline(session,(_,_,_,_)=>Task.FromResult("{\"units\":[[0,\"KEEP\",\"Synthetic\",\"chapter\",\"\"]]}"));
        var first=await pipe.AnalyzeAsync(input,["c"],new Progress<ImportProgress>(),default);
        using var copy=session.ForkForAssembly(default);
        Assert.AreNotEqual(session.State.ProjectId,copy.State.ProjectId); Assert.AreEqual("",copy.State.PlannedPath);
        var cached=await new ImportPipeline(copy,(_,_,_,_)=>throw new AssertFailedException("First pass repeated")).AnalyzeAsync(input,["c"],new Progress<ImportProgress>(),default);
        CollectionAssert.AreEqual(first,cached); Assert.AreEqual(e.Id,LiteraryProjectStore.ReadProject(e.ProjectPath).Id);
        Assert.IsTrue(Path.GetFileName(copy.Root).StartsWith("Импорт DeepSeek "));
    }
}
