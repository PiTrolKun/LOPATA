using System.IO;
using System.Collections.Specialized;
using System.Text.Json;
using System.Windows;
using AIHub.Controls;
using AIHub.Services;
using Clipboard = System.Windows.Clipboard;

namespace AIHub.Tests;

public sealed partial class LetItOutChatUiTests
{
    [TestMethod]
    public Task NativeComposerImportsClipboardImageFilesAndRealDroppedFile() => Sta(() =>
    {
        if (Environment.GetEnvironmentVariable("LOPATA_LETITOUT_COMPOSER_NATIVE") != "1")
            Assert.Inconclusive("Opt in to installed WebView2 and temporary clipboard use.");
        var directory = HistoryDirectory(); var clipboard = Clipboard.GetDataObject();
        try
        {
            var store = new LetItOutConversationStore(Path.Combine(directory, "chat.db"));
            using var fixture = new Fixture("ru", true, store);
            fixture.Control.ConfigureFlowInput(); fixture.Window.UpdateLayout();
            var browser = fixture.Descendants.OfType<LetItOutComposerBrowser>().Single();
            Pump(() => browser.IsReady, 60000);
            fixture.Input.Text = "Сохрани этот текст";
            Pump(() => JsonSerializer.Deserialize<string>(Script("document.getElementById('edit').innerText")) == fixture.Input.Text);
            Script("composer.focus();true");
            var path = Path.Combine(directory, "Файл из буфера.txt"); File.WriteAllText(path, "Обычная заметка.");
            Clipboard.SetFileDropList(new StringCollection { path });
            PasteFiles(); Pump(() => DraftCount() == 1);
            Assert.AreEqual("Сохрани этот текст", fixture.Input.Text);
            var image = new System.Windows.Media.Imaging.WriteableBitmap(8, 8, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
            Clipboard.SetImage(image); PasteFiles(); Pump(() => DraftCount() == 2);
            Assert.AreEqual("Сохрани этот текст", fixture.Input.Text);

            // CDP assigns an actual OS file to a DOM File; the production drop listener must
            // deliver its native CoreWebView2File.Path through AdditionalObjects.
            Script("const probe=document.createElement('input');probe.type='file';probe.id='file-probe';document.body.appendChild(probe);true");
            using var dom = JsonDocument.Parse(Protocol("DOM.getDocument", "{}"));
            var root = dom.RootElement.GetProperty("root").GetProperty("nodeId").GetInt32();
            using var node = JsonDocument.Parse(Protocol("DOM.querySelector", JsonSerializer.Serialize(new { nodeId = root, selector = "#file-probe" })));
            Protocol("DOM.setFileInputFiles", JsonSerializer.Serialize(new { nodeId = node.RootElement.GetProperty("nodeId").GetInt32(), files = new[] { path } }));
            Script("const transfer=new DataTransfer();transfer.items.add(document.getElementById('file-probe').files[0]);document.getElementById('edit').dispatchEvent(new DragEvent('drop',{dataTransfer:transfer,bubbles:true,cancelable:true}));true");
            Pump(() => DraftCount() == 3);
            Assert.AreEqual("Файл из буфера.txt", store.Attachment(fixture.Control.SessionId, store.Load(fixture.Control.SessionId)!.DraftAttachments![2]).Name);
            Assert.AreEqual("Сохрани этот текст", fixture.Input.Text);
            Script("const plain=new DataTransfer();plain.setData('text/plain',' плюс текст');document.getElementById('edit').dispatchEvent(new ClipboardEvent('paste',{clipboardData:plain,bubbles:true,cancelable:true}));true");
            Pump(() => fixture.Input.Text == "Сохрани этот текст плюс текст");
            Assert.AreEqual(3, DraftCount()); Assert.AreEqual(0, fixture.Runtime.Requests.Count);
            int DraftCount() => store.Load(fixture.Control.SessionId)?.DraftAttachments?.Count ?? 0;
            void PasteFiles() => Script("const transfer=new DataTransfer();transfer.items.add(new File(['x'],'clipboard.png'));document.getElementById('edit').dispatchEvent(new ClipboardEvent('paste',{clipboardData:transfer,bubbles:true,cancelable:true}));true");
            string Script(string source) { var task = browser.ExecuteScriptAsync("{" + source + "}"); Pump(() => task.IsCompleted, 30000); return task.GetAwaiter().GetResult(); }
            string Protocol(string method, string args) { var task = browser.CoreWebView2.CallDevToolsProtocolMethodAsync(method, args); Pump(() => task.IsCompleted, 30000); return task.GetAwaiter().GetResult(); }
        }
        finally { if (clipboard is not null) Clipboard.SetDataObject(clipboard, true); else Clipboard.Clear(); DeleteHistoryDirectory(directory); }
    });
}
