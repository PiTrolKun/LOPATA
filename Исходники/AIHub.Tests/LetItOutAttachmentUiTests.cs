using System.IO;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

public sealed partial class LetItOutChatUiTests
{
    [TestMethod, DataRow("ru", true), DataRow("en", false)]
    public Task AttachmentOnlySendRestoresDraftAndKeepsFilesOnTheirOwnTurn(string language, bool dark) => Sta(() =>
    {
        var directory = HistoryDirectory();
        try
        {
            var store = new LetItOutConversationStore(Path.Combine(directory, "history.db"));
            using var fixture = new Fixture(language, dark, store);
            var path = Path.Combine(directory, "Заметка.txt"); File.WriteAllText(path, "Материал о работе и отдыхе.");
            var importing = fixture.Control.AddAttachmentPathsAsync([path]); Pump(() => importing.IsCompleted); importing.GetAwaiter().GetResult();
            var id = fixture.Control.SessionId; var a = store.Load(id)!.DraftAttachments!.Single();
            Assert.IsTrue(fixture.Button("LetItOut.Send").IsEnabled, "A file can be sent without explanatory text.");
            Assert.AreEqual("", fixture.Input.Text);
            var second = Guid.NewGuid().ToString("N"); store.Create(second);
            Assert.IsTrue(fixture.Control.OpenConversation(second)); Assert.IsFalse(fixture.Button("LetItOut.Send").IsEnabled);
            Assert.IsTrue(fixture.Control.OpenConversation(id)); Assert.IsTrue(fixture.Button("LetItOut.Send").IsEnabled);
            fixture.Click("LetItOut.Send"); Pump(() => fixture.Runtime.Requests.Count == 1);
            Assert.AreEqual(a, fixture.Runtime.Requests[0].Messages.Single().Attachments!.Single());
            Assert.HasCount(0, store.Load(id)!.DraftAttachments!);
            fixture.Runtime.Complete(0, "Что ты хотел обсудить в этой заметке?"); Pump(() => !fixture.Control.IsWorking);
            Assert.AreEqual(a, store.Load(id)!.Messages[0].Attachments!.Single());
            Assert.IsTrue(fixture.Descendants.OfType<Button>().Any(b => AutomationProperties.GetAutomationId(b) == "LetItOut.Attachment." + a));
            fixture.Input.Text = "Продолжим"; fixture.Click("LetItOut.Send"); Pump(() => fixture.Runtime.Requests.Count == 2);
            Assert.AreEqual(a, fixture.Runtime.Requests[1].Messages[0].Attachments!.Single());
            Assert.IsTrue(fixture.Runtime.Requests[1].Messages[^1].Attachments is null or { Count: 0 });
            fixture.Runtime.Complete(1, "Слушаю."); Pump(() => fixture.Runtime.TitleReplies.Count == 1);
            fixture.Runtime.TitleReplies[0].TrySetResult("Работа и отдых"); Pump(() => !fixture.Control.IsWorking);
        }
        finally { DeleteHistoryDirectory(directory); }
    });
}
