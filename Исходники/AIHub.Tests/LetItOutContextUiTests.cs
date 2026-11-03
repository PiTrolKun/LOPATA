using System.IO;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

public sealed partial class LetItOutChatUiTests
{
    [TestMethod]
    public Task ContextMeterAndManualReservePreserveTheDraftAndFullHistory() => Sta(() =>
    {
        var directory=HistoryDirectory();
        try
        {
            var store=new LetItOutConversationStore(Path.Combine(directory,"history.db"));
            var id=Guid.NewGuid().ToString("N"); store.Create(id);
            store.SaveContent(id,[new("user","First question"),new("assistant","First answer"),new("user","Latest question"),new("assistant","Latest answer")],"Draft to preserve");
            using var fixture=new Fixture("ru",true,store,context:true);
            Pump(()=>fixture.Control.ContextInfo is not null);
            Assert.AreEqual(60d,fixture.Control.ContextInfo!.Status.FillPercent); Assert.IsTrue(fixture.Control.CanTrimContext);
            var trim=fixture.Control.TrimContextAsync(); Pump(()=>trim.IsCompleted); trim.GetAwaiter().GetResult();
            Assert.AreEqual(50d,fixture.Control.ContextInfo!.Status.FillPercent);
            Assert.AreEqual("Draft to preserve",fixture.Input.Text); Assert.HasCount(4,store.Load(id)!.Messages);
            Assert.AreEqual("Draft to preserve",fixture.Runtime.TrimRequests.Single().Messages[^1].Content);
            fixture.Click("LetItOut.Send"); Pump(()=>fixture.Control.IsWorking);
            Assert.IsFalse(fixture.Control.CanTrimContext); fixture.Runtime.Complete(0,"Answer after trim");
            fixture.Control.SetConversationTitle(id,"Manual test title"); Pump(()=>!fixture.Control.IsWorking);
        }
        finally { DeleteHistoryDirectory(directory); }
    });

    [TestMethod]
    public Task LateContextMeasurementCannotOverwriteTheNewTopic() => Sta(() =>
    {
        var directory=HistoryDirectory();
        try
        {
            var store=new LetItOutConversationStore(Path.Combine(directory,"history.db"));
            using var fixture=new Fixture("ru",true,store,context:true);
            Pump(()=>fixture.Control.ContextInfo is not null);
            var previous=fixture.Runtime.ContextRequests.Count;
            fixture.Runtime.DelayedContext=new(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Input.Text="Old topic draft"; Pump(()=>fixture.Runtime.ContextRequests.Count>previous);
            var late=fixture.Runtime.DelayedContext; fixture.Runtime.DelayedContext=null;
            fixture.Click("LetItOut.New"); var current=fixture.Control.SessionId;
            late.SetResult(new(9900,10000,true,false));
            Pump(()=>fixture.Control.ContextInfo is not null);
            Assert.AreEqual(6000,fixture.Control.ContextInfo!.UsedTokens);
            Assert.AreEqual(current,fixture.Control.SessionId); Assert.AreEqual("",fixture.Input.Text);
        }
        finally { DeleteHistoryDirectory(directory); }
    });
}
