using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using AIHub.Models;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using MenuItem = System.Windows.Controls.MenuItem;

namespace AIHub.Tests;

public sealed partial class LetItOutChatUiTests
{
    [TestMethod, DataRow("ru", true), DataRow("en", false)]
    public Task ContextMenuExportsTheClickedTopicWithoutModelCallsAndSourcesOpenSeparately(string language, bool dark) => Sta(() =>
    {
        var root = HistoryDirectory();
        try
        {
            var store = new LetItOutConversationStore(Path.Combine(root, "private", "history.db"));
            var a = Guid.NewGuid().ToString("N"); var b = Guid.NewGuid().ToString("N"); store.Create(a); store.SetManualTitle(a, "Исходная тема");
            store.SaveContent(a, [new("user", "Код ZETA-4827"), new("assistant", "Запомнили код.")], "Не отправлено");
            store.Create(b); store.SaveContent(b, [new("user", "Помнишь код?"), new("assistant", "В старой теме был ZETA-4827.")], "Черновик остаётся");
            var hit = store.SearchMemories(b, "ZETA", default).First(); store.SaveRecallLinks(b, 1, [hit]);
            using var fixture = new Fixture(language, dark, store); Assert.IsTrue(fixture.Control.OpenConversation(b));
            var output = Path.Combine(root, "выбранная.zip"); fixture.Control.ExportDestination = _ => output;
            var row = fixture.Descendants.OfType<Button>().Single(v => AutomationProperties.GetAutomationId(v) == "LetItOut.Topic." + a);
            var export = row.ContextMenu!.Items.OfType<MenuItem>().Single(v => AutomationProperties.GetAutomationId(v) == "LetItOut.Library.Export");
            export.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump(() => File.Exists(output));
            Assert.AreEqual(b, fixture.Control.SessionId); Assert.HasCount(0, fixture.Runtime.Requests);
            using (var zip = ZipFile.OpenRead(output))
            using (var data = JsonDocument.Parse(new StreamReader(zip.GetEntry("conversation.json")!.Open()).ReadToEnd()))
            {
                Assert.AreEqual(a, data.RootElement.GetProperty("conversation").GetProperty("Id").GetString());
                Assert.IsFalse(data.RootElement.GetProperty("map").GetProperty("available").GetBoolean());
                Assert.IsFalse(data.RootElement.GetProperty("summary").GetProperty("available").GetBoolean());
            }
            var source = fixture.Descendants.OfType<Button>().Single(v => AutomationProperties.GetAutomationId(v) == "LetItOut.Recall." + a + ".1");
            source.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var window = fixture.Window.OwnedWindows.OfType<Window>().Single(w => AutomationProperties.GetAutomationId(w) == "LetItOut.Recall.Window");
            Assert.AreEqual(b, fixture.Control.SessionId); window.Close();
            Assert.AreEqual("Черновик остаётся", store.Load(b)!.Draft);
        }
        finally { DeleteHistoryDirectory(root); }
    });
}
