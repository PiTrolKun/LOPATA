using System.Text.Json;
using System.Text.Json.Nodes;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class LiteraryParagraphTests
{
    private string _root="";
    private LiteraryProject _project=null!;
    private LiteraryChapterStore _chapters=null!;
    private LiteraryProjectLayout Layout=>new(_root);
    private LiteraryEditorSnapshot Editor=>LiteraryEditorSnapshot.Capture(_project.Id,_root,_chapters.Index,"Свежий черновик ещё не сохранён.",true);
    private LiteraryParagraphCatalog Catalog=>new(_project,Editor,k=>k);
    [TestInitialize] public void Setup()
    {
        _root=Path.Combine(Path.GetTempPath(),"lopata-paragraph-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root);
        _project=new() { CreationBrief="""
            {"sections":[{"Topic":1,"Step":4,"Text":"СЕКРЕТ_ЗАМЫСЛА","Question":"Идея"},{"Topic":3,"Text":"Персонаж Мирон","Question":"Кто"}],"route":[{"Number":1,"Title":"Будущая станция","Description":"ПОТОМ_НА_МАРС"}]}
            """,Premise="НЕ_ВКЛЮЧАТЬ_АВТОМАТИЧЕСКИ",Include="СКРЫТОЕ_УСЛОВИЕ" };
        File.WriteAllText(Path.Combine(_root,"project.json"),JsonSerializer.Serialize(_project)); Layout.Initialize();
        _chapters=new(_root); _chapters.Open(); _chapters.Save("Ключ перешёл к Мирону. Законченная глава."); _chapters.Finish();
        _chapters.Save("Старый текст на диске.");
    }
    [TestCleanup] public void Cleanup() => Directory.Delete(_root,true);
    private Task<ParagraphEvidence> Read(Dictionary<string,ParagraphSelection> selection,Action<ParagraphReceipt>? progress=null)
        =>new LiteraryParagraphSources(_project,Editor,Catalog).ReadAsync("Продолжи сцену с Мироном",selection,progress??(_=>{}),default);
    private static ParagraphSelection Check(string comment="")=>new(){Selected=true,Comment=comment};

    [TestMethod] public async Task WriterGetsOnlyFinalTaskWhileScreenHistoryRemainsIntact()
    {
        var history=new[]{new ParagraphTurn("User","СТАРАЯ_ПРОСЬБА"),new ParagraphTurn("Advisor","УЕХАТЬ_В_РОССИЮ"),new ParagraphTurn("Writer","СТАРОЕ_ПРЕДЛОЖЕНИЕ")};
        var r=new ParagraphRequest(LiteraryChatProfile.Writer,"МОЯ_ТОЧНАЯ_ПРАВКА",Editor,history,new Dictionary<string,ParagraphSelection>(),"route/1","s");
        var evidence=await Read([]);
        var packet=LiteraryParagraphPrompts.Build(r,evidence,Catalog).Last().Content;
        foreach(var turn in history) Assert.IsFalse(packet.Contains(turn.Text));
        StringAssert.Contains(packet,"МОЯ_ТОЧНАЯ_ПРАВКА"); StringAssert.Contains(packet,Editor.Text);
        Assert.AreEqual(3,history.Length);
        var advisor=LiteraryParagraphPrompts.Build(r with{Role=LiteraryChatProfile.Advisor},evidence,Catalog).Last().Content;
        foreach(var turn in history) StringAssert.Contains(advisor,turn.Text);
    }

    [TestMethod] public async Task CurrentRouteIsReadableWithoutSelectingTheFutureRouteResource()
    {
        _project.CreationBrief="""{"sections":[],"route":[{"Number":1,"Title":"Детство","Description":"Только первые эксперименты"},{"Number":2,"Title":"Учёба","Description":"СЕКРЕТ_БУДУЩЕГО"}]}""";
        var r=new ParagraphRequest(LiteraryChatProfile.Writer,"Начни",Editor,[],new Dictionary<string,ParagraphSelection>(),"route/1","s");
        var evidence=await Read([]);
        var packet=JsonNode.Parse(LiteraryParagraphPrompts.Build(r,evidence,Catalog).Last().Content)!;
        Assert.AreEqual("Детство",packet["current_route"]!["Title"]!.ToString());
        Assert.AreEqual("Только первые эксперименты",packet["current_route"]!["Description"]!.ToString());
        Assert.IsFalse(packet.ToJsonString().Contains("СЕКРЕТ_БУДУЩЕГО")); Assert.AreEqual(0,evidence.Materials.Count);
        var changed=JsonNode.Parse(LiteraryParagraphPrompts.Build(r with{RouteId="route/2"},evidence,Catalog).Last().Content)!;
        Assert.AreEqual("Учёба",changed["current_route"]!["Title"]!.ToString());
        Assert.Throws<IOException>(()=>LiteraryParagraphPrompts.Build(r with{RouteId="route/99"},evidence,Catalog));
        Assert.IsNull(JsonNode.Parse(LiteraryParagraphPrompts.Build(r with{RouteId=""},evidence,Catalog).Last().Content)!["current_route"]);
    }

    [TestMethod] public void ParametersUseCalibratedValuesAndSupportLegacyAndExpertProjects()
    {
        var l=new LocalizationService(); l.Load("ru");
        _project.Form="novel"; _project.Genres=["fantasy"];
        _project.CreationBrief=ParagraphJson.Encode(new{sections=new object[]{
            new{Topic=1,Step=5,Text="Повесть после калибровки"},
            new{Topic=1,Step=6,Text=""},
            new{Topic=6,Question=l.T("Literary.Interview.Q29"),Text="Неторопливо"},
            new{Topic=6,Step=28,Text="АДАПТИВНОЕ_НЕ_ПОДМЕНЯЕТ",Adaptive=true}}});
        var values=LiteraryProjectParameters.Read(_project,l.T);
        Assert.AreEqual("Повесть после калибровки",values.Single(v=>v.Step==5).Value);
        Assert.AreEqual(l.T("Literary.Parameters.Unspecified"),values.Single(v=>v.Step==6).Value);
        Assert.AreEqual("Неторопливо",values.Single(v=>v.Step==29).Value);
        Assert.IsFalse(values.Any(v=>v.Value.Contains("АДАПТИВНОЕ_НЕ_ПОДМЕНЯЕТ")));
        _project.CreationBrief="";
        values=LiteraryProjectParameters.Read(_project,l.T);
        Assert.AreEqual(l.T("Literary.Form.novel"),values.Single(v=>v.Step==5).Value);
        Assert.AreEqual(l.T("Literary.Interview.Genre.fantasy"),values.Single(v=>v.Step==6).Value);
    }

    [TestMethod] public async Task CompactPacketKeepsAllSelectedTextAndReceiptLinksWithoutHashCopies()
    {
        _project.CreationBrief=ParagraphJson.Encode(new { sections=Enumerable.Range(0,39).Select(i=>new
            { Topic=1+i%7, Question="Уточнение "+i, Text="Дословный ответ №"+i+": sourceRevision остаётся словом автора." }) });
        var selection=new Dictionary<string,ParagraphSelection>{["creation"]=Check(),["creation/field/0"]=Check("Особенно важно")};
        var evidence=await Read(selection); var before=ParagraphJson.Encode(evidence);
        var request=new ParagraphRequest(LiteraryChatProfile.Advisor,"Начинаем рассказ",Editor,[],selection,"","test");
        var packet=JsonNode.Parse(LiteraryParagraphPrompts.Build(request,evidence,Catalog).Last().Content)!;
        var materials=packet["materials"]!.AsArray(); Assert.AreEqual(39,materials.Count);
        var originals=evidence.Materials.Select(m=>JsonNode.Parse(ParagraphJson.Encode(m.Data))!["text"]!.ToString()).ToArray();
        CollectionAssert.AreEquivalent(originals,materials.Select(m=>m!["data"]!["text"]!.ToString()).ToArray());
        Assert.IsTrue(materials.All(m=>m!["kind"]!.ToString()=="confirmed_creation_intent"));
        Assert.IsTrue(materials.All(m=>m!["data"]!["label"] is not null));
        var ids=materials.Select(m=>m!["id"]!.ToString()).ToHashSet();
        Assert.AreEqual(39,ids.Count);
        Assert.IsTrue(packet["receipts"]!.AsArray().SelectMany(r=>r!["materials"]!.AsArray()).All(id=>ids.Contains(id!.ToString())));
        Assert.AreEqual(2,packet["receipts"]!.AsArray().Count);
        Assert.IsFalse(packet["available"]!.AsArray().Any(n=>n!["id"]!.ToString().StartsWith("creation")));
        Assert.IsTrue(packet["available"]!.AsArray().Any(n=>n!["id"]!.ToString()=="jelly"));
        Assert.IsFalse(packet.ToJsonString().Contains(evidence.Materials[0].Revision));
        Assert.AreEqual(before,ParagraphJson.Encode(evidence));
    }

    [TestMethod] public async Task CommentsFollowTheActualTreeWhenFieldIdsAreNotNestedUnderTopicIds()
    {
        var selection=new Dictionary<string,ParagraphSelection>{["creation/field/0"]=Check(),
            ["creation/topic/1"]=new(){Comment="Контекст раздела"},["creation/topic/3"]=new(){Comment="Чужой комментарий"}};
        var packet=ParagraphJson.Encode(LiteraryParagraphPacket.Build(new(LiteraryChatProfile.Advisor,"Просьба",Editor,[],selection,"",""),await Read(selection),Catalog));
        StringAssert.Contains(packet,"Контекст раздела"); Assert.IsFalse(packet.Contains("Чужой комментарий"));
    }

    [TestMethod] public async Task UncheckedDataCannotLeakThroughLegacyContext()
    {
        var evidence=await Read([]);
        var request=new ParagraphRequest(LiteraryChatProfile.Writer,"ТОЧНАЯ_ПРАВКА",Editor,[],new Dictionary<string,ParagraphSelection>(),"","session");
        var packet=LiteraryParagraphPrompts.Build(request,evidence,Catalog).Last().Content;
        foreach(var absent in new[]{"СЕКРЕТ_ЗАМЫСЛА","ПОТОМ_НА_МАРС","НЕ_ВКЛЮЧАТЬ_АВТОМАТИЧЕСКИ","СКРЫТОЕ_УСЛОВИЕ","Старый текст на диске"}) Assert.IsFalse(packet.Contains(absent),absent);
        StringAssert.Contains(packet,"ТОЧНАЯ_ПРАВКА"); StringAssert.Contains(packet,Editor.Text); Assert.AreEqual(0,evidence.Receipts.Count);
    }
    [TestMethod] public async Task ParentAndChildEachHaveReceiptAndOverlapsAreDeduplicated()
    {
        var selection=new Dictionary<string,ParagraphSelection> { ["creation"]=Check("Без смены замысла"),["creation/field/0"]=Check() };
        var result=await Read(selection);
        Assert.IsTrue(result.Complete); Assert.AreEqual(2,result.Receipts.Count); Assert.AreEqual(2,result.Materials.Count);
        Assert.AreEqual("Без смены замысла",result.Receipts[1].Comment);
        CollectionAssert.IsSubsetOf(result.Receipts[1].Materials,result.Receipts[0].Materials);
        Assert.IsFalse(ParagraphJson.Encode(result).Contains("ПОТОМ_НА_МАРС"));
    }
    [TestMethod] public async Task ChildDoesNotRequireParentAndRouteRemainsIntent()
    {
        var result=await Read(new(){["route/1/description"]=Check()});
        Assert.AreEqual(1,result.Materials.Count); Assert.AreEqual("future_route",result.Materials[0].Kind);
        StringAssert.Contains(ParagraphJson.Encode(result),"ПОТОМ_НА_МАРС");
        Assert.IsFalse(ParagraphJson.Encode(result).Contains("СЕКРЕТ_ЗАМЫСЛА"));
    }
    [TestMethod] public async Task MissingAndEmptySourcesAreDifferentAndFailureBlocksPrompt()
    {
        var result=await Read(new(){["anchors/Writer/include"]=Check(),["deleted/id"]=Check()});
        Assert.AreEqual("empty",result.Receipts[0].Status); Assert.AreEqual("error",result.Receipts[1].Status); Assert.IsFalse(result.Complete);
        Assert.Throws<IOException>(()=>LiteraryParagraphPrompts.Build(new(LiteraryChatProfile.Advisor,"task",Editor,[],new Dictionary<string,ParagraphSelection>(),"",""),result,Catalog));
    }
    [TestMethod] public async Task CompletedPartReadExcludesActiveFileAndDetectsMissingSource()
    {
        var id=Catalog.Roots.Single(r=>r.Id=="chapters").Children.Single().Id;
        var result=await Read(new(){[id]=Check()}); StringAssert.Contains(ParagraphJson.Encode(result),"Ключ перешёл к Мирону");
        Assert.IsFalse(ParagraphJson.Encode(result).Contains("Старый текст на диске"));
        File.Delete(Path.Combine(_root,"chapters",_chapters.Index.Parts[0].FileName));
        Assert.AreEqual("error",(await Read(new(){[id]=Check()})).Receipts.Single().Status);
    }
    [TestMethod] public void SessionRoundtripClearAndInterruptedDoNotTouchProject()
    {
        var store=new LiteraryParagraphStore(Layout); var state=store.Load(); var before=File.ReadAllText(Path.Combine(_root,"project.json"));
        state.Stage=ParagraphStage.Prepared; state.Prepared="Моя правка"; state.Request="Просьба"; state.Interrupted=true;
        state.Selection["creation/field/0"]=Check("Не забудь"); state.History.Add(new("Writer","Предложение")); store.Save(state);
        var loaded=store.Load(); Assert.AreEqual(ParagraphStage.Prepared,loaded.Stage); Assert.AreEqual("Моя правка",loaded.Prepared); Assert.IsTrue(loaded.Interrupted);
        var session=loaded.SessionId; loaded.Clear(); store.Save(loaded); loaded=store.Load();
        Assert.AreNotEqual(session,loaded.SessionId); Assert.AreEqual(ParagraphStage.Request,loaded.Stage); Assert.AreEqual(0,loaded.History.Count);
        Assert.AreEqual("",loaded.Prepared); Assert.AreEqual("Не забудь",loaded.Selection.Values.Single().Comment);
        Assert.AreEqual(before,File.ReadAllText(Path.Combine(_root,"project.json"))); Assert.AreEqual("Старый текст на диске.",_chapters.Load());
    }
    [TestMethod] public void RecommendationsAreAllowlistedAndDoNotSelectOrRead()
    {
        var reply=LiteraryParagraphPrompts.ParseAdvisor("""{"task":"Моя задача","recommendations":[{"id":"jelly","reason":"Проверить состояние"},{"id":"shell","reason":"Недопустимо"}]}""",Catalog);
        Assert.AreEqual("Моя задача",reply.Task); Assert.AreEqual(1,reply.Recommendations.Length); Assert.AreEqual("jelly",reply.Recommendations[0].Id);
    }
    [TestMethod] public void WriterFormatRejectsMultipleParagraphsWithoutRewriting()
    {
        Assert.IsTrue(LiteraryParagraphPrompts.IsSingleParagraph("Один абзац. Ещё предложение.\n"));
        foreach(var text in new[]{"", " \n", "Первый\nВторой","Первый\r\n\r\nВторой","Первый\u2029Второй"}) Assert.IsFalse(LiteraryParagraphPrompts.IsSingleParagraph(text));
    }
    [TestMethod] public void RevisionTracksContentAndIgnoresSessionAndDiagnostics()
    {
        var before=LiteraryParagraphRevision.Capture(Editor);
        new LiteraryParagraphStore(Layout).Save(new(){ProjectId=_project.Id});
        Assert.AreEqual(before,LiteraryParagraphRevision.Capture(Editor));
        File.AppendAllText(Path.Combine(_root,"project.json")," ");
        Assert.AreNotEqual(before,LiteraryParagraphRevision.Capture(Editor));
    }
    [TestMethod] public void InvalidSessionIsNotSilentlyOverwritten()
    {
        var store=new LiteraryParagraphStore(Layout); Layout.EnsureFolder("Dialogs/ParagraphFlow");
        File.WriteAllText(store.FilePath,"{broken"); Assert.Throws<JsonException>(()=>store.Load()); Assert.AreEqual("{broken",File.ReadAllText(store.FilePath));
    }
    [TestMethod] public async Task DeepJellyScopeChecksSourceRevisionAndKeepsFactType()
    {
        var part=_chapters.Index.Parts[0]; var text=File.ReadAllText(Path.Combine(_root,"chapters",part.FileName));
        var batch=new LiteraryJellyBatch(Guid.NewGuid().ToString("N"),part.Id,"001",LiteraryWorkIndex.Revision(text),text,
            [new(){Subject="Мирон",Relation="получил",Value="ключ",Kind="event",Evidence="Ключ перешёл к Мирону."}]);
        var jelly=new LiteraryJellyStore(Layout); jelly.Stage(batch); jelly.Confirm(batch,batch.Facts);
        var leaf=Catalog.Nodes.Values.Single(n=>n.Kind=="jellyPart");
        var result=await Read(new(){[leaf.Id]=Check()}); Assert.IsTrue(result.Complete); Assert.AreEqual(1,result.Materials.Count);
        StringAssert.Contains(ParagraphJson.Encode(result),"получил"); Assert.AreEqual("confirmed_project_memory",result.Materials[0].Kind);
        File.AppendAllText(Path.Combine(_root,"chapters",part.FileName)," Текст был изменён.");
        Assert.AreEqual("error",(await Read(new(){[leaf.Id]=Check()})).Receipts.Single().Status);
    }
    [TestMethod] public async Task CancellationDoesNotClaimUnexecutedScopesWereRead()
    {
        using var ct=new CancellationTokenSource(); ct.Cancel(); var progress=new List<ParagraphReceipt>();
        await Assert.ThrowsAsync<OperationCanceledException>(()=>new LiteraryParagraphSources(_project,Editor,Catalog).ReadAsync("запрос",new Dictionary<string,ParagraphSelection>{["creation"]=Check(),["jelly"]=Check()},progress.Add,ct.Token));
        Assert.AreEqual(2,progress.Count); Assert.IsTrue(progress.All(r=>r.Status=="not_read"));
    }
}
