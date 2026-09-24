using System.Globalization;
using System.Text;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.LiteraryImport;

namespace AIHub.Tests;

[TestClass]
public sealed partial class ImportJellyPreparationTests
{
    private string _root = "", _project = "";
    private ImportSession _session = null!;
    private ImportWorkingPartsPreparation Service => new(_session);
    private static readonly IProgress<ImportWorkingPartsProgress> Quiet = new InlineProgress<ImportWorkingPartsProgress>(_ => { });

    [TestInitialize] public async Task Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "lopata-import-jelly-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "private.json"); File.WriteAllText(source, "[]");
        _session = ImportSession.Create(_root, source, default); _session.State.SelectedProject = "Book";
        var input = new ImportInput([new("c", "chat", 1)], [new("u", "c", "m", "", "RESPONSE", 0, 0, "OLD PRIVATE TEXT", false)], [], []);
        _project = ImportProjectBuilder.Build(_session, input, [new("u", "MAIN", "Book", "Chapter", "")],
            new LiteraryProjectStore(Path.Combine(_root, "projects.json")), _root, "Book", "test", "en").ProjectPath;
        SetBook(new string('a', 16000) + "😀е\u0301 END");
        _session.State.Stage = "book-confirmed"; _session.State.RagStatus = "ready"; _session.Save();
        MakeRag();
        await Service.PrepareAsync("auto", Quiet, default);
    }
    [TestCleanup] public void Cleanup()
    {
        _session.Dispose();
        if (Path.GetFullPath(_root).StartsWith(Path.Combine(Path.GetTempPath(), "lopata-import-jelly-"), StringComparison.OrdinalIgnoreCase)) Directory.Delete(_root, true);
    }
    private ImportReviewBook Book() => new ImportReviewBookStore(_project).Load();
    private void SetBook(string text)
    {
        var store = new ImportReviewBookStore(_project); var b = store.Load(); b.ReplaceText(text); b.Headings.Clear(); store.Save(b);
    }
    private void MakeRag()
    {
        var book = Book(); var revision = ImportSession.Hash(book.Text);
        var folder = Path.Combine(_project, "Rag/ImportIndexes/Book", revision); Directory.CreateDirectory(folder);
        var input = Path.Combine(folder, "text.json"); var output = Path.Combine(folder, "vectors.jsonl");
        File.WriteAllText(input, JsonSerializer.Serialize(new[] { new ImportRagSection(book.ProjectId, "book", book.Text, "project") }));
        var scalar = book.Text.EnumerateRunes().ToArray(); var vector = new float[1024]; vector[0] = 1;
        var lines = new List<string>();
        for (var i = 0; i < scalar.Length; i += 1900)
        {
            var text = string.Concat(scalar.Skip(i).Take(2000).Select(r => r.ToString()));
            lines.Add(JsonSerializer.Serialize(new { id = lines.Count + 1, vector,
                payload = new { source = book.ProjectId, section = "book", offset = i, text, kind = "project" } }));
        }
        File.WriteAllLines(output, lines);
        var manifest = new ImportRagManifest("Book", revision, new string('b', 32), lines.Count,
            GigaEmbeddingInstallation.Revision, ImportSession.HashFile(input), ImportSession.HashFile(output), book.Text.Length);
        Directory.CreateDirectory(Path.Combine(_project, "Rag/Book"));
        File.WriteAllText(Path.Combine(_project, "Rag/Book/manifest.json"), JsonSerializer.Serialize(manifest));
    }
    private ImportJellyPreparation Jelly => new(_session, "runeweaver");
    private LiteraryJellyStore Memory => new(new LiteraryProjectLayout(_project));
    private static readonly IProgress<ImportJellyProgress> Progress = new InlineProgress<ImportJellyProgress>(_ => { });
    private static Task<string> Empty(string text, CancellationToken token) => Task.FromResult("{\"facts\":[]}");
    private static Task<string> InvalidContent(string text, CancellationToken token) => Task.FromResult(JsonSerializer.Serialize(new { facts = new[] { new { subject = "", relation = "", value = "draft", kind = "unknown", evidence = "NOT IN BOOK" } } }));

    [TestMethod] public async Task AutoAcceptsFirstContentWarningsAndKeepsSources()
    {
        var before = Directory.GetFiles(Path.Combine(_project,"WorkingParts"),"*",SearchOption.AllDirectories).ToDictionary(p=>p,ImportSession.HashFile);
        var calls=0;
        var result=await Jelly.RunAsync("auto",(s,t)=>{ calls++; return InvalidContent(s,t); },Progress,default);
        Assert.IsTrue(result.Ready); Assert.AreEqual("ready",_session.State.MemoryStatus);
        Assert.IsTrue(Memory.Read().Count>0); Assert.IsTrue(Memory.Read().All(f=>f.Fact.Evidence=="NOT IN BOOK"));
        Assert.AreEqual(Service.Current()!.Parts.Sum(p=>LiteraryJellyContract.Chunks(Book().Text.Substring(p.Start,p.Length)).Count()),calls);
        Assert.IsTrue(JsonSerializer.Serialize(Memory.ExportAudit()).Contains("auto_saved"));
        foreach(var (file,hash) in before) Assert.AreEqual(hash,ImportSession.HashFile(file));
        Assert.IsTrue((await Jelly.RunAsync("auto",(_,_)=>throw new Exception("Must not regenerate"),Progress,default)).Ready);
    }
    [TestMethod] public async Task ManualCanEditResumeAndAcceptWarningsWithSeparateMode()
    {
        Jelly.SaveMode("manual"); Assert.AreEqual("manual",Jelly.LoadState()!.Mode); Assert.AreEqual("auto",Service.Current()!.Mode);
        var first=await Jelly.RunAsync("manual",InvalidContent,Progress,default);
        Assert.IsFalse(first.Ready); Assert.IsNotNull(first.Review); Assert.HasCount(0,Memory.Read());
        var facts=first.Review.Facts.Select(f=>f with { Value="User edit" }).ToArray();
        Jelly.SaveReviewDraft(first.Review,facts);
        var resumed=await Jelly.RunAsync("manual",(_,_)=>throw new Exception("Must use saved proposal"),Progress,default);
        Assert.AreEqual("User edit",resumed.Review!.Facts[0].Value);
        Assert.IsNull(await Jelly.ConfirmAsync(resumed.Review,facts,true,default));
        Assert.IsTrue(Memory.Read().All(e=>e.Fact.Value=="User edit"));
        Assert.IsTrue(JsonSerializer.Serialize(Memory.ExportAudit()).Contains("user_approved_warnings"));
        Assert.IsFalse(Jelly.LoadState()!.Status=="ready");
    }
    [TestMethod] public async Task EmptyFactsCompleteWithoutManualDialog()
    {
        var result=await Jelly.RunAsync("manual",Empty,Progress,default);
        Assert.IsTrue(result.Ready); Assert.HasCount(0,Memory.Read()); Assert.AreEqual("ready",Jelly.LoadState()!.Status);
    }
    [TestMethod] public async Task FourTechnicalAttemptsProduceOneCleanReport()
    {
        var calls=0;
        var result=await Jelly.RunAsync("auto",(_,_)=>{ calls++; throw new IOException("SECRET BOOK "+_project); },Progress,default);
        Assert.IsFalse(result.Ready); Assert.AreEqual(4,calls); Assert.IsNotNull(result.Report);
        Assert.HasCount(1,Directory.GetFiles(Path.GetDirectoryName(result.Report)!));
        var text=File.ReadAllText(result.Report); Assert.IsFalse(text.Contains("SECRET BOOK")); Assert.IsFalse(text.Contains(_project));
        Assert.AreEqual(4,JsonDocument.Parse(text).RootElement.GetProperty("attempts").GetArrayLength());
        Assert.AreEqual("failed",Jelly.LoadState()!.Status);
    }
    [TestMethod] public async Task MalformedJsonRetriesButFourthResultSucceeds()
    {
        var calls=0;
        var result=await Jelly.RunAsync("auto",(s,t)=>++calls<=3?Task.FromResult("broken"):Empty(s,t),Progress,default);
        Assert.IsTrue(result.Ready); Assert.IsTrue(calls>3);
    }
    [TestMethod] public async Task CancellationKeepsSuccessfulChunkAndResumesWithoutReextracting()
    {
        using var cancel=new CancellationTokenSource(); var seen=new List<string>(); var calls=0;
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(()=>Jelly.RunAsync("auto",(s,t)=>
        { if(++calls==2) { cancel.Cancel(); t.ThrowIfCancellationRequested(); } seen.Add(s);return Empty(s,t); },Progress,cancel.Token));
        Assert.AreEqual(1,seen.Count); Assert.AreEqual("stopped",Jelly.LoadState()!.Status);
        var after=0; Assert.IsTrue((await Jelly.RunAsync("auto",(s,t)=>{after++;return Empty(s,t);},Progress,default)).Ready);
        var total=Service.Current()!.Parts.Sum(p=>LiteraryJellyContract.Chunks(Book().Text.Substring(p.Start,p.Length)).Count());
        Assert.AreEqual(total-1,after);
    }
    [TestMethod] public async Task ChangedBookBlocksConfirmation()
    {
        var pending=(await Jelly.RunAsync("manual",InvalidContent,Progress,default)).Review!;
        SetBook(Book().Text+" changed");
        Assert.IsNotNull(await Jelly.ConfirmAsync(pending,pending.Facts,true,default)); Assert.HasCount(0,Memory.Read());
    }
    [TestMethod] public async Task LockedStateReportsWriteFailureWithoutGenerating()
    {
        Jelly.SaveMode("auto");
        using var locked=new FileStream(Path.Combine(_project,"Import/jelly-state.json"),FileMode.Open,FileAccess.Read,FileShare.None);
        var calls=0; var result=await Jelly.RunAsync("auto",(s,t)=>{calls++;return Empty(s,t);},Progress,default);
        Assert.IsFalse(result.Ready); Assert.IsNotNull(result.Report); Assert.AreEqual(0,calls);
    }
    [TestMethod] public async Task ModelContentCannotChangeFactIdentity()
    {
        var batch=(await Jelly.RunAsync("manual",InvalidContent,Progress,default)).Review!;
        var other=batch.Facts.Select(f=>f with { Id=Guid.NewGuid().ToString("N") }).ToArray();
        Assert.ThrowsExactly<InvalidDataException>(()=>Jelly.SaveReviewDraft(batch,other));
    }
    [TestMethod] public void MissingFieldsAndExtraFactsAreContentWarnings()
    {
        var rows=ImportJellyResponse.Parse(JsonSerializer.Serialize(new { facts=Enumerable.Range(0,15).Select(i=>new { subject="Person" }) }));
        Assert.HasCount(15,rows); Assert.IsTrue(rows.All(f=>LiteraryJellyValidation.Check(f,"source").Count>0));
        Assert.ThrowsExactly<InvalidDataException>(()=>ImportJellyResponse.Parse("{}"));
    }
    [TestMethod] public void LegacyPendingBatchSerializationRemainsCompatible()
    {
        var batch=new LiteraryJellyBatch("b","p","1","r","text",[]);
        var old=JsonSerializer.Serialize(new {batch.Id,batch.PartId,batch.Number,batch.Revision,batch.SourceText,batch.Facts,batch.Status});
        Assert.AreEqual(old,JsonSerializer.Serialize(JsonSerializer.Deserialize<LiteraryJellyBatch>(old)));
    }
    [TestMethod] public void DirectFactArraysRemainReadableButMalformedRowsDoNot()
    {
        Assert.HasCount(0,ImportJellyResponse.Parse("[]"));
        Assert.HasCount(0,ImportJellyResponse.Parse("{\"facts\":[]}"));
        Assert.HasCount(1,ImportJellyResponse.Parse("[{\"subject\":\"Alice\"}]"));
        foreach(var raw in new[]{"{}","null","[null]","[1]","{\"facts\":null}"})
            Assert.ThrowsExactly<InvalidDataException>(()=>ImportJellyResponse.Parse(raw));
        foreach(var divider in new[]{"---\n\n\n\n"," * * * ","___","\r\n"})
            Assert.IsTrue(ImportJellyResponse.IsSeparator(divider));
        foreach(var text in new[]{"— Да.","---\nAlice left.","**Name**","123","😀"})
            Assert.IsFalse(ImportJellyResponse.IsSeparator(text));
    }
    [TestMethod] public async Task SeparatorTailResumesExistingCheckpointsWithoutGeneratingOrChangingBook()
    {
        var text=new string('a',1584)+"\n"+new string('b',1597)+"\n---\n\n\n\n";
        SetBook(text); MakeRag(); await Service.PrepareAsync("auto",Quiet,default);
        var manifest=Service.Current()!; Assert.HasCount(1,manifest.Parts);
        var part=manifest.Parts[0]; var chunks=LiteraryJellyContract.Chunks(text).ToArray();
        Assert.HasCount(3,chunks); Assert.AreEqual("---\n\n\n\n",chunks[2]);
        var folder=Path.Combine(_project,"Jelly/ImportStaging",manifest.Generation,"runeweaver-v1",part.Id);
        Directory.CreateDirectory(folder);
        var ids=new List<string>();
        for(var i=0;i<2;i++)
        {
            var fact=new LiteraryJellyFact {Subject="Person",Relation="did",Value="something",Evidence=chunks[i]}; ids.Add(fact.Id);
            File.WriteAllText(Path.Combine(folder,$"{i:000}.json"),JsonSerializer.Serialize(new[]{fact}));
        }
        Assert.IsTrue((await Jelly.RunAsync("auto",(_,_)=>throw new Exception("No extraction expected"),Progress,default)).Ready);
        CollectionAssert.AreEquivalent(ids,Memory.Read().Select(f=>f.Id).ToList());
        Assert.AreEqual(text,Book().Text);
        Assert.AreEqual(text,File.ReadAllText(Path.Combine(_project,"WorkingParts",manifest.Generation,part.Id+".txt")));
        Assert.IsTrue((await Jelly.RunAsync("auto",(_,_)=>throw new Exception("No regeneration expected"),Progress,default)).Ready);
    }
    [TestMethod] public async Task IdenticalPartsInNewGenerationReuseFacts()
    {
        Assert.IsTrue((await Jelly.RunAsync("auto",InvalidContent,Progress,default)).Ready);
        var generation=Service.Current()!.Generation;
        var draft=(await Service.PrepareAsync("manual",Quiet,default)).Plan!;
        Assert.IsTrue((await Service.CommitAsync(draft,Quiet,default)).Ready);
        Assert.AreNotEqual(generation,Service.Current()!.Generation);
        Assert.IsTrue((await Jelly.RunAsync("auto",(_,_)=>throw new Exception("Must reuse identical text"),Progress,default)).Ready);
    }
}
