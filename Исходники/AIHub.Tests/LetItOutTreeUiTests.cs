using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

public sealed partial class LetItOutChatUiTests
{
    [TestMethod]
    [DataRow("ru",true)]
    [DataRow("en",false)]
    public Task CollapsedTreeReturnsWidthToChatAndKeepsDraftAndPerTopicPreference(string language,bool dark)=>Sta(()=>
    {
        var directory=HistoryDirectory();
        try
        {
            var store=new LetItOutConversationStore(Path.Combine(directory,"history.db"));var id=Guid.NewGuid().ToString("N");store.Create(id);
            using var fixture=new Fixture(language,dark,store);fixture.Window.Width=1800;fixture.Window.UpdateLayout();
            Assert.IsFalse(store.Tree(id).Enabled);fixture.Click("LetItOut.Tree.Toggle");fixture.Window.UpdateLayout();
            var chat=(Grid)fixture.Descendants.OfType<ScrollViewer>().Single(s=>System.Windows.Automation.AutomationProperties.GetAutomationId(s)=="LetItOut.History").Parent;
            var expanded=chat.ActualWidth;fixture.Input.Text="Draft preserved while toggling";
            fixture.Click("LetItOut.Tree.Toggle");fixture.Window.UpdateLayout();
            Assert.IsTrue(chat.ActualWidth>expanded+150);Assert.IsFalse(store.Tree(id).Enabled);
            Assert.AreEqual("Draft preserved while toggling",fixture.Input.Text);
            fixture.Click("LetItOut.Tree.Toggle");fixture.Window.UpdateLayout();Assert.IsTrue(store.Tree(id).Enabled);
            Assert.AreEqual(expanded,chat.ActualWidth,1);
            fixture.Click("LetItOut.Tree.Toggle");fixture.Click("LetItOut.New");Assert.IsTrue(fixture.Control.OpenConversation(id));
            Assert.IsFalse(store.Tree(id).Enabled);Assert.AreEqual("Draft preserved while toggling",fixture.Input.Text);
        }
        finally{DeleteHistoryDirectory(directory);}
    });

    [TestMethod]
    public Task NewUserInputPreemptsTreeBeforeStartingTheChatRequest()=>Sta(()=>
    {
        var directory=HistoryDirectory();
        try
        {
            var store=new LetItOutConversationStore(Path.Combine(directory,"history.db"));var id=Guid.NewGuid().ToString("N");store.Create(id);
            store.SaveContent(id,[new("user","Первое"),new("assistant","Ответ"),new("user","Второе"),new("assistant","Ответ")],"");
            using var fixture=new Fixture("ru",true,store);var runtime=new TreeGateRuntime();
            fixture.Control.ConfigureRuntime(()=>"test",new(runtime),()=>"ru");
            typeof(AIHub.Controls.LetItOutChatControl).GetMethod("TryBuildTree",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(fixture.Control,null);
            Assert.IsFalse(runtime.Started,"A map must not run before the user explicitly enables it.");
            fixture.Click("LetItOut.Tree.Toggle");
            typeof(AIHub.Controls.LetItOutChatControl).GetMethod("TryBuildTree",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(fixture.Control,null);
            Pump(()=>runtime.Started);fixture.Input.Text="Мой следующий вопрос";fixture.Click("LetItOut.Send");
            Pump(()=>runtime.Answered);Assert.IsTrue(runtime.CanceledBeforeAnswer);
            Assert.AreEqual(5,runtime.LastRequest!.Messages.Count);Assert.IsFalse(runtime.LastRequest.Messages.Any(m=>m.Content.Contains("tree")));
            Pump(()=>!fixture.Control.IsWorking);
        }
        finally{DeleteHistoryDirectory(directory);}
    });
    private sealed class TreeGateRuntime:ILetItOutChatRuntime,ILetItOutTreeRuntime
    {
        internal bool Started,Canceled,Answered,CanceledBeforeAnswer;internal int Attempts;internal LetItOutChatRequest? LastRequest;
        public async Task UpdateTreeAsync(LetItOutTreeRequest request,BackgroundOperationState? restored,CancellationToken token)
        {
            await ApplicationBackgroundOperations.RunAsync(LetItOutChatRuntime.TreeWorkKind,"Tree",request.ConversationId,request,async ct=>
            {Attempts++;Started=true;try{await Task.Delay(Timeout.Infinite,ct);}finally{Canceled=true;}return true;},token);
        }
        public Task<string> GenerateAsync(LetItOutChatRequest request,IProgress<ModelStreamChunk> progress,CancellationToken token)
        {LastRequest=request;CanceledBeforeAnswer=Canceled;Answered=true;return Task.FromResult("Ответ пользователю");}
        public Task RetireAsync()=>Task.CompletedTask;
    }

    [TestMethod]
    public Task MapWorkUsesSharedPauseResumeAndCollapsingStopsItWithoutLosingSources()=>Sta(()=>
    {
        var directory=HistoryDirectory();
        try
        {
            var store=new LetItOutConversationStore(Path.Combine(directory,"history.db"));var id=Guid.NewGuid().ToString("N");store.Create(id);
            store.SaveContent(id,[new("user","Первая история"),new("assistant","Ответ"),new("user","Уточнение"),new("assistant","Ответ")],"");
            store.SetTreeEnabled(id,true);using var fixture=new Fixture("ru",true,store);var runtime=new TreeGateRuntime();
            fixture.Control.ConfigureRuntime(()=>"test",new(runtime),()=>"ru");
            typeof(AIHub.Controls.LetItOutChatControl).GetMethod("TryBuildTree",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(fixture.Control,null);
            Pump(()=>runtime.Attempts==1);var pause=fixture.Controller.PauseAsync();
            Pump(()=>pause.IsCompleted&&fixture.Controller.State?.Phase==BackgroundOperationPhase.Paused);
            Assert.AreEqual(1,runtime.Attempts);var resume=fixture.Controller.ResumeAsync(CancellationToken.None);
            Pump(()=>runtime.Attempts==2);fixture.Click("LetItOut.Tree.Toggle");
            Pump(()=>!fixture.Controller.BlocksNewWork&&resume.IsCompleted);
            Assert.IsFalse(store.Tree(id).Enabled);Assert.IsNull(store.Tree(id).Document);
            Assert.AreEqual(4,store.Load(id)!.Messages.Count);
        }
        finally{DeleteHistoryDirectory(directory);}
    });

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public Task MapNodeOpensItsExactOriginalPair(bool dark)=>Sta(()=>
    {
        using var fixture=new Fixture("en",dark);
        foreach(var key in fixture.Control.Resources.Keys)fixture.Window.Resources[key]=fixture.Control.Resources[key];
        fixture.Window.Resources["HeaderBackgroundBrush"]=fixture.Control.Resources["PanelBrush"];
        var root=new LetItOutTreeNode("root","","Topic","","theme","reported",5,[]);
        var node=new LetItOutTreeNode("event","root","Date","29 November","event","reported",4,[1]);
        var document=new LetItOutTreeDocument("LOPATA","test","let_it_out.topic_tree","test",Guid.NewGuid().ToString("N"),1,1,DateTimeOffset.UtcNow,"Topic",[root,node]);
        var pair=new LetItOutMemoryPair(1,0,1,"Original user words","Original model words",DateTimeOffset.UtcNow,DateTimeOffset.UtcNow,"fingerprint",null,false);
        var map=new AIHub.Controls.LetItOutTopicMapWindow(document,[pair],fixture.Localize,"event",fixture.Window){ShowActivated=false,ShowInTaskbar=false};
        try
        {
            map.Show();map.UpdateLayout();
            var texts=ScenarioNavigationTests.LogicalDescendants(map).OfType<System.Windows.Controls.TextBox>().Select(t=>t.Text).ToArray();
            Assert.IsTrue(texts.Any(t=>t.Contains(pair.User)));Assert.IsTrue(texts.Any(t=>t.Contains(pair.Answer)));
            Assert.AreEqual(((System.Windows.Media.SolidColorBrush)fixture.Control.Resources["PanelBrush"]).Color,((System.Windows.Media.SolidColorBrush)map.Background).Color);
            var button=ScenarioNavigationTests.LogicalDescendants(map).OfType<System.Windows.Controls.Button>().Single(b=>System.Windows.Automation.AutomationProperties.GetAutomationId(b)=="LetItOut.Map.Node.event");
            Assert.AreEqual(((System.Windows.Media.SolidColorBrush)fixture.Control.Resources["TextPrimaryBrush"]).Color,((System.Windows.Media.SolidColorBrush)((TextBlock)button.Content).Foreground).Color);
            var panel=new AIHub.Controls.LetItOutTopicTreePanel();panel.Resources.MergedDictionaries.Add(fixture.Control.Resources);
            panel.Update(document,fixture.Localize,false,false);
            var graph=ScenarioNavigationTests.LogicalDescendants(panel).OfType<Canvas>().Single();
            Assert.AreEqual(1,graph.Children.OfType<System.Windows.Shapes.Path>().Count());
            Assert.AreEqual(2,graph.Children.OfType<System.Windows.Controls.Button>().Count());
        }
        finally{map.Close();}
    });
}
