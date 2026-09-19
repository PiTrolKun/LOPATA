using System.Reflection;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class LiteraryStudioTests
{
    private string _root="";
    private LiteraryProject _project=null!;
    private LiteraryChapterStore _chapters=null!;
    [TestInitialize] public void Setup()
    {
        _root=Path.Combine(Path.GetTempPath(),"lopata-studio-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(_root);
        _project=new(){Genres=["comedy"],CreationBrief="""{"sections":[{"Topic":1,"Step":4,"Text":"UNSELECTED_SECRET"},{"Topic":6,"Step":29,"Text":"SLOW_PACING"}],"route":[{"Number":1,"Title":"LOCAL_ROUTE","Description":"ONE_MOMENT"},{"Number":2,"Title":"FUTURE_SECRET","Description":"Later"}]}"""};
        File.WriteAllText(Path.Combine(_root,"project.json"),JsonSerializer.Serialize(_project));new LiteraryProjectLayout(_root).Initialize();
        _chapters=new(_root);_chapters.Open();_chapters.Save("CURRENT_DRAFT");
    }
    [TestCleanup] public void Cleanup()=>Directory.Delete(_root,true);
    private LiteraryEditorSnapshot Editor=>LiteraryEditorSnapshot.Capture(_project.Id,_root,_chapters.Index,_chapters.Load(),false);
    private StudioRequest Request(LiteraryChatProfile role,string action)=>new(new(role,"LOCAL_TASK",Editor,[],new Dictionary<string,ParagraphSelection>(),"route/1","session",true),
        action,[new(){Role="Advisor",Text="PRIVATE_ADVISOR_CHAT"}],[new("Explicit quote","QUOTE_TEXT")],"TRANSFERRED_TASK","TARGET_VARIANT",["KEEP_TONE"],false);
    private string Packet(StudioRequest request)=>LiteraryStudioPrompts.Build(request,new([],[]),new(_project,Editor,k=>k),_project,k=>k).Last().Content;

    [TestMethod] public void WriterPacketOmitsDialogueAndUnselectedMaterialButRetainsTaskAndPacing()
    {
        var text=Packet(Request(LiteraryChatProfile.Writer,"Rewrite"));
        foreach(var expected in new[]{"TRANSFERRED_TASK","CURRENT_DRAFT","QUOTE_TEXT","TARGET_VARIANT","KEEP_TONE","LOCAL_ROUTE","SLOW_PACING"}) StringAssert.Contains(text,expected);
        foreach(var absent in new[]{"PRIVATE_ADVISOR_CHAT","UNSELECTED_SECRET","FUTURE_SECRET"}) Assert.IsFalse(text.Contains(absent));
    }
    [TestMethod] public void AdvisorAndTransferReceiveCurrentConversation()
    {
        foreach(var action in new[]{"Discuss","Transfer"}) StringAssert.Contains(Packet(Request(LiteraryChatProfile.Advisor,action)),"PRIVATE_ADVISOR_CHAT");
    }
    [TestMethod] public void ContinuationBasisChangesWithoutMakingChatCanonical()
    {
        var request=Request(LiteraryChatProfile.Writer,"Continue");
        using var fromEditor=JsonDocument.Parse(Packet(request));
        Assert.AreEqual("CURRENT_DRAFT",fromEditor.RootElement.GetProperty("continuation_basis").GetString());
        using var fromChat=JsonDocument.Parse(Packet(request with {ContinueFromChat=true}));
        Assert.AreEqual("TARGET_VARIANT",fromChat.RootElement.GetProperty("continuation_basis").GetString());
        StringAssert.Contains(fromChat.RootElement.GetProperty("packet").GetRawText(),"CURRENT_DRAFT");
    }
    [TestMethod] public void SuccessfulTransferClearsAdvisorButKeepsJournal()
    {
        var state=new LiteraryStudioState();state.Add("User","old");state.Add("Advisor","idea");state.Transfer("task");
        Assert.AreEqual(3,state.Messages.Count);Assert.AreEqual(1,state.Messages.Count(m=>m.InContext));
        state.Add("Writer","result");state.Result="result";state.ReturnToAdvisor();
        CollectionAssert.AreEqual(new[]{"Task","Writer"},state.Messages.Where(m=>m.InContext).Select(m=>m.Role).ToArray());
        state.Clear();Assert.AreEqual(4,state.Messages.Count);Assert.IsFalse(state.Messages.Any(m=>m.InContext));Assert.AreEqual("",state.Result);
    }
    [TestMethod] public void HandoffPreferenceSurvivesReloadAndClearAndOldSessionsHaveADefault()
    {
        var store=new LiteraryStudioStore(new(_root)); var state=store.Load();
        Assert.IsTrue(state.DirectRequest);
        Assert.AreEqual(StudioSendKeyAction.Advisor,state.EnterAction);
        Assert.AreEqual(StudioSendKeyAction.Writer,state.ControlEnterAction);
        state.EnterAction=StudioSendKeyAction.NewLine; state.ControlEnterAction=StudioSendKeyAction.CurrentRole;
        state.DirectRequest=false; state.Transfer("task"); store.Save(state);
        state=new LiteraryStudioStore(new(_root)).Load();
        Assert.IsFalse(state.DirectRequest); state.Clear(); Assert.IsFalse(state.DirectRequest);
        Assert.AreEqual(StudioSendKeyAction.NewLine,state.EnterAction); Assert.AreEqual(StudioSendKeyAction.CurrentRole,state.ControlEnterAction);
        var old=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(store.FilePath))!.AsObject();
        old.Remove("DirectRequest"); old.Remove("EnterAction"); old.Remove("ControlEnterAction"); File.WriteAllText(store.FilePath,old.ToJsonString());
        var restored=new LiteraryStudioStore(new(_root)).Load();
        Assert.IsTrue(restored.DirectRequest); Assert.AreEqual("task",restored.Messages.Single().Text);
        Assert.AreEqual(StudioSendKeyAction.Advisor,restored.EnterAction); Assert.AreEqual(StudioSendKeyAction.Writer,restored.ControlEnterAction);
    }
    [TestMethod] public void ContextIconsReflectWriterAction()
    {
        var state=new LiteraryStudioState();state.Transfer("task");state.Result="result";var message=state.Add("Writer","result");
        Assert.IsFalse(LiteraryStudioContext.Contains(state,message));state.ContinueFromChat=true;Assert.IsTrue(LiteraryStudioContext.Contains(state,message));
        state.ContinueFromChat=false;state.Action="Rewrite";Assert.IsTrue(LiteraryStudioContext.Contains(state,message));
        message.Complete=false;Assert.IsFalse(LiteraryStudioContext.Contains(state,message));
    }
    [TestMethod] public void CustomPromptsRemainScopedToActionAndTransferKeepsItsSchema()
    {
        var request=Request(LiteraryChatProfile.Writer,"Tone") with{CustomPrompt="CUSTOM_ACTION",CustomRolePrompt="CUSTOM_ROLE"};
        var messages=LiteraryStudioPrompts.Build(request,new([],[]),new(_project,Editor,k=>k),_project,k=>k);
        StringAssert.Contains(messages[0].Content,"CUSTOM_ACTION");StringAssert.Contains(messages[0].Content,"CUSTOM_ROLE");StringAssert.Contains(messages[0].Content,"одного абзаца");
        var transfer=LiteraryStudioPrompts.Build(request with{Base=request.Base with{Role=LiteraryChatProfile.Advisor},Action="Transfer"},new([],[]),new(_project,Editor,k=>k),_project,k=>k);
        Assert.IsFalse(transfer[0].Content.Contains("CUSTOM_ROLE"));StringAssert.Contains(transfer[0].Content,"JSON");
    }
    [TestMethod] public void CalibratingSameProjectDoesNotBreakChapterSaving()
    {
        var calibration=new LiteraryCalibrationStore(_root);calibration.Read();calibration.Save("new intent");
        _chapters.Save("LOCAL_EDIT");Assert.AreEqual("LOCAL_EDIT",_chapters.Load());
        _project.Id=Guid.NewGuid().ToString("N");File.WriteAllText(Path.Combine(_root,"project.json"),JsonSerializer.Serialize(_project));
        Assert.Throws<IOException>(()=>_chapters.Save("must not save"));Assert.AreEqual("LOCAL_EDIT",_chapters.Load());
    }
    [TestMethod] public async Task QueuedFreeChatCancellationNeverCreatesTranscriptOrDiagnostics()
    {
        LiteraryRequestDiagnostics.Enabled=true;
        using var runtime=new LiteraryChatRuntime(_root);
        var gate=(SemaphoreSlim)typeof(LiteraryChatRuntime).GetField("_gate",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(runtime)!;
        await gate.WaitAsync();
        try
        {
            var before=Directory.GetFiles(_root,"*",SearchOption.AllDirectories).Order().ToArray();
            using var ct=new CancellationTokenSource();
            var pending=runtime.FreeChatAsync([new(){Role="user",Content="PRIVATE_FREE_SENTINEL"}],new InlineProgress<ModelStreamChunk>(_=>{}),ct.Token);
            Assert.IsFalse(pending.IsCompleted);Assert.IsFalse(runtime.IsBusy);ct.Cancel();
            try {await pending;Assert.Fail("Expected cancellation.");} catch(OperationCanceledException) { }
            CollectionAssert.AreEqual(before,Directory.GetFiles(_root,"*",SearchOption.AllDirectories).Order().ToArray());
            Assert.AreEqual(0,gate.CurrentCount);
        }
        finally {gate.Release();LiteraryRequestDiagnostics.Enabled=false;}
    }
    [TestMethod] public async Task FreeChatRejectsSystemMessagesBeforeAcquiringQueue()
    {
        using var runtime=new LiteraryChatRuntime(_root);
        try {await runtime.FreeChatAsync([new(){Role="system",Content="project data"}],new InlineProgress<ModelStreamChunk>(_=>{}),default);Assert.Fail();}
        catch(ArgumentException) { }
        Assert.IsFalse(runtime.IsBusy);
    }
    [TestMethod] public async Task StoppingRuntimeCancelsAllPendingRequestsWithoutLoadingModel()
    {
        using var runtime=new LiteraryChatRuntime(_root);
        var gate=(SemaphoreSlim)typeof(LiteraryChatRuntime).GetField("_gate",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(runtime)!;
        await gate.WaitAsync();
        try
        {
            var first=runtime.FreeChatAsync([new(){Role="user",Content="A"}],new InlineProgress<ModelStreamChunk>(_=>{}),default);
            var second=runtime.FreeChatAsync([new(){Role="user",Content="B"}],new InlineProgress<ModelStreamChunk>(_=>{}),default);
            runtime.Stop();
            foreach(var pending in new[]{first,second}) {try {await pending;Assert.Fail();}catch(OperationCanceledException){}}
            Assert.AreEqual(0,gate.CurrentCount);Assert.IsFalse(runtime.IsBusy);
        }
        finally {gate.Release();}
    }
    [TestMethod] public void InterruptedRagCommitRecoversAllArtifactsTogether()
    {
        var layout=new LiteraryProjectLayout(_root);var id=Guid.NewGuid().ToString("N");
        var folder=layout.EnsureFolder("Rag/Source");var backup=layout.EnsureFolder("Rag/Source/Edits/"+id+"/before");
        var original=JsonSerializer.Serialize(new[]{new LiterarySourceSection("reference","section","old text")});
        foreach(var name in new[]{"text.json","vectors.jsonl","manifest.json"}) {File.WriteAllText(Path.Combine(backup,name),name=="text.json"?original:"old");File.WriteAllText(Path.Combine(folder,name),"half committed");}
        File.WriteAllText(Path.Combine(folder,"editing.json"),id);
        var result=new LiteraryRagEditorStore(layout).Read();Assert.AreEqual("old text",result[0].Text);
        Assert.AreEqual("old",File.ReadAllText(Path.Combine(folder,"vectors.jsonl")));Assert.IsFalse(File.Exists(Path.Combine(folder,"editing.json")));
    }
    [TestMethod] public void ConcurrentHistoryWritersCannotOverwriteEachOther()
    {
        var first=new LiteraryStudioStore(new(_root));var a=first.Load();
        var second=new LiteraryStudioStore(new(_root));var b=second.Load();a.Add("User","keep this");first.Save(a);
        b.Add("User","stale write");Assert.Throws<IOException>(()=>second.Save(b));
        Assert.AreEqual("keep this",new LiteraryStudioStore(new(_root)).Load().Messages.Single().Text);
    }
}
