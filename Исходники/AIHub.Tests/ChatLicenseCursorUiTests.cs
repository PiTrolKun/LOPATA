using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using Point = System.Windows.Point;

namespace AIHub.Tests;

public sealed partial class LetItOutChatUiTests
{
    [TestMethod]
    public Task NativeImageSendOpensDecisionAfterBrowserCallbackWithVisiblePointer() => Sta(() =>
    {
        if (Environment.GetEnvironmentVariable("LOPATA_LETITOUT_COMPOSER_NATIVE") != "1")
            Assert.Inconclusive("Opt in to WebView2 and a temporary pointer-position probe.");
        var directory = HistoryDirectory(); GetPointerPosition(out var oldPoint);
        var oldCursor = ReadPointer();
        try
        {
            var store = new LetItOutConversationStore(Path.Combine(directory, "history.db"));
            using var fixture = new Fixture("ru", true, store);
            fixture.Control.ConfigureFlowInput(); fixture.Window.UpdateLayout();
            var browser = fixture.Descendants.OfType<LetItOutComposerBrowser>().Single();
            Pump(() => browser.IsReady, 60000);
            var image = Path.Combine(directory, "image.png");
            var bitmap = new RenderTargetBitmap(16, 16, 96, 96, PixelFormats.Pbgra32);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(image)) png.Save(file);
            var importing = fixture.Control.AddAttachmentPathsAsync([image]); Pump(() => importing.IsCompleted); importing.GetAwaiter().GetResult();
            var callbackFinished = false; var openedAfterCallback = false; var dialogs = 0;
            var visiblePointers = new List<bool>(); var accept = false; var switchTopic = false;
            browser.CoreWebView2.WebMessageReceived += (_, e) =>
            {
                using var message = JsonDocument.Parse(e.WebMessageAsJson);
                if (message.RootElement.GetProperty("type").GetString() != "send") return;
                callbackFinished = true;
                if (switchTopic) { fixture.Click("LetItOut.New"); switchTopic = false; }
            };
            fixture.Control.ConfigureAttachments(_ =>
            {
                openedAfterCallback = callbackFinished; dialogs++;
                if (!openedAfterCallback) return Task.FromResult(false);
                var window = new Window { Owner = fixture.Window, Width = 400, Height = 200,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner, Title = "License cursor probe", Background = Brushes.White };
                var button = new Button { Content = accept ? "Accept" : "Cancel", Padding = new(20), Margin = new(30) };
                button.Click += (_, _) => window.DialogResult = accept; window.Content = button;
                ModalDialogPointer.Configure(window);
                window.ContentRendered += (_, _) => window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    var pointer = ReadPointer(); visiblePointers.Add((pointer.Flags & 1) != 0 && pointer.Handle != 0);
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }));
                window.SourceInitialized += (_, _) =>
                {
                    var center = fixture.Window.PointToScreen(new Point(fixture.Window.ActualWidth / 2, fixture.Window.ActualHeight / 2));
                    SetPointerPosition((int)center.X, (int)center.Y);
                    SetNativeCursor(0); // Recreate a native browser-to-modal transition with no cursor shape.
                };
                return Task.FromResult(window.ShowDialog() == true);
            }, _ => { });

            SendFromBrowser(); Pump(() => dialogs == 1 && visiblePointers.Count == 1);
            Assert.IsTrue(openedAfterCallback, "Modal UI must start after every WebMessageReceived subscriber returned.");
            Assert.IsTrue(visiblePointers[0], "The native cursor must be visible inside the decision window.");
            Assert.HasCount(0, fixture.Runtime.Requests, "Declining terms does not send or lose the attachment.");
            Assert.HasCount(1, store.Load(fixture.Control.SessionId)!.DraftAttachments!);
            accept = true; SendFromBrowser(); Pump(() => fixture.Runtime.Requests.Count == 1);
            Assert.IsTrue(visiblePointers[1]); Assert.HasCount(1, fixture.Runtime.Requests[0].Messages[^1].Attachments!);
            fixture.Runtime.Complete(0, "Вижу вложение."); Pump(() => !fixture.Control.IsWorking);
            fixture.Input.Text = "После окна можно писать";
            Pump(() => JsonSerializer.Deserialize<string>(Script("document.getElementById('edit').innerText")) == fixture.Input.Text);
            var original = fixture.Control.SessionId; switchTopic = true; SendFromBrowser();
            Pump(() => fixture.Control.SessionId != original);
            var drained = fixture.Window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); Pump(() => drained.Task.IsCompleted);
            Assert.HasCount(1, fixture.Runtime.Requests, "A queued send cannot cross into a different conversation.");

            void SendFromBrowser() { callbackFinished = false; Script("composer.focus();document.getElementById('edit').dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',bubbles:true,cancelable:true}));true"); }
            string Script(string source) { var task = browser.ExecuteScriptAsync(source); Pump(() => task.IsCompleted, 30000); return task.GetAwaiter().GetResult(); }
        }
        finally { SetNativeCursor(oldCursor.Handle); SetPointerPosition(oldPoint.X, oldPoint.Y); DeleteHistoryDirectory(directory); }
    });

    private static PointerInfo ReadPointer()
    {
        var info = new PointerInfo { Size = Marshal.SizeOf<PointerInfo>() };
        if (!GetPointerInfo(ref info)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return info;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PointerPoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct PointerInfo { public int Size, Flags; public nint Handle; public PointerPoint Position; }
    [DllImport("user32.dll", EntryPoint = "GetCursorInfo", SetLastError = true)] private static extern bool GetPointerInfo(ref PointerInfo info);
    [DllImport("user32.dll", EntryPoint = "GetCursorPos")] private static extern bool GetPointerPosition(out PointerPoint point);
    [DllImport("user32.dll", EntryPoint = "SetCursorPos")] private static extern bool SetPointerPosition(int x, int y);
    [DllImport("user32.dll", EntryPoint = "SetCursor")] private static extern nint SetNativeCursor(nint cursor);
}
