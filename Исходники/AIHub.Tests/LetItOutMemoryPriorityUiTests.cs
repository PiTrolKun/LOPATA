using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

public sealed partial class LetItOutChatUiTests
{
    [TestMethod]
    public Task WaitingMemoryIsParkedWithItsCheckpointAndDoesNotBlockTheNextQuestion()=>Sta(()=>
    {
        var directory=HistoryDirectory();
        try
        {
            var store=new LetItOutConversationStore(Path.Combine(directory,"history.db"));var id=Guid.NewGuid().ToString("N");store.Create(id);
            store.SaveContent(id,[new("user","Первая реплика"),new("assistant","Первый ответ")],"Сохранённый черновик");
            using var fixture=new Fixture("ru",true,store);
            var checkpoint=JsonSerializer.SerializeToElement(new{Through=1,Expanded="Уже готовая часть сводки"});
            var state=new BackgroundOperationState{Kind=LetItOutChatRuntime.MemoryWorkKind,Project=id,Title="Memory",
                Phase=BackgroundOperationPhase.Waiting,RequiresDecision=true,Detail="LetItOut.VramChanged",Private=true,
                Input=JsonSerializer.SerializeToElement(new LetItOutMemoryCloseRequest(id,Guid.NewGuid().ToString("N"),"test","ru")),Checkpoint=checkpoint};
            fixture.Store.Save(state);fixture.Controller.Load();Assert.IsTrue(fixture.Controller.BlocksNewWork);
            fixture.Input.Text="Новая реплика после ожидания";
            Pump(()=>!fixture.Controller.BlocksNewWork&&fixture.Button("LetItOut.Send").IsEnabled);
            var parked=fixture.Controller.FindOperations(LetItOutChatRuntime.MemoryWorkKind,id).Single();
            Assert.IsTrue(parked.UserPaused);Assert.IsFalse(parked.RequiresDecision);
            Assert.AreEqual(1,parked.Checkpoint!.Value.GetProperty("Through").GetInt32());
            Assert.AreEqual(checkpoint.GetProperty("Expanded").GetString(),parked.Checkpoint.Value.GetProperty("Expanded").GetString());
            fixture.Click("LetItOut.Send");Pump(()=>fixture.Runtime.Requests.Count==1);
            Assert.AreEqual("Новая реплика после ожидания",fixture.Runtime.Requests[0].Messages[^1].Content);
            fixture.Runtime.Complete(0,"Продолжение разговора");Pump(()=>fixture.Runtime.TitleRequests.Count==1);
            fixture.Runtime.TitleReplies[0].SetResult("Продолжение");Pump(()=>!fixture.Control.IsWorking);
            Assert.IsNotNull(fixture.Controller.FindOperations(LetItOutChatRuntime.MemoryWorkKind,id).Single());
            Assert.AreEqual(4,store.Load(id)!.Messages.Count);
        }
        finally{DeleteHistoryDirectory(directory);}
    });
}
