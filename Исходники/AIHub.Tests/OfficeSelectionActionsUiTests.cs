using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Controls.OfficeReader;
using AIHub.Controls.OfficeTextEditor;
using AIHub.Services;
using AIHub.Services.OfficeEditor;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class OfficeSelectionActionsUiTests
{
    [TestMethod]
    public Task ContextMenuSearchRephraseManualEditUndoAndStaleSelection()
    {
        var proof = Environment.GetEnvironmentVariable("LOPATA_SELECTION_UI");
        if (string.IsNullOrWhiteSpace(proof)) Assert.Inconclusive("Opt in to the real editor and selected model.");
        return OfficeProjectUiThread.RunAsync(async () =>
        {
            Directory.CreateDirectory(proof);
            var folder = Directory.CreateTempSubdirectory("lopata-rephrase-");
            var source = Path.Combine(folder.FullName, "source.docx");
            const string sentence = "Привет, я отправлю вам документы завтра в 10 часов.";
            var initial = OfficeTextFormats.PlainDocument(sentence + "\nОставить эту строку прежней.");
            File.WriteAllBytes(source, initial);
            var loc = new LocalizationService(); loc.Load("ru");
            var window = new ReaderWindowGroup(null, loc.T, true).OpenEditor(source);
            OfficeRephraseWindow? dialog = null;
            try
            {
                await Wait(() => Find<OfficeTextBrowser>(window).Any(b => b.IsVisible && b.CoreWebView2 != null));
                var browser = Find<OfficeTextBrowser>(window).First(b => b.IsVisible);
                await Wait(() => Find<Button>(window).Any(b => AutomationProperties.GetAutomationId(b) == "Office.Editor.Summary" && b.IsEnabled));
                var view = Find<OfficeTextEditorView>(window).Single();
                Uri? searched = null; view.SearchBrowser = uri => searched = uri;
                JsonElement context = default;
                browser.Message += message => { if (message.GetProperty("type").GetString() == "selectionAction") context = message; };
                await browser.RequestAsync(new { action = "find", text = sentence }, default);
                async Task Menu(string action)
                {
                    var opened = await browser.CoreWebView2.ExecuteScriptAsync("document.querySelector('#viewport').dispatchEvent(new MouseEvent('contextmenu',{bubbles:true,cancelable:true,clientX:200,clientY:200})); document.querySelectorAll('.lopata-spelling-menu [data-action]').length");
                    Assert.AreEqual("2", opened);
                    await browser.CoreWebView2.ExecuteScriptAsync("document.querySelector('[data-action=" + action + "]').click()");
                }
                await Menu("search"); await Wait(() => searched != null);
                Assert.AreEqual(sentence, Uri.UnescapeDataString(searched!.Query[3..]));
                await Menu("rephrase");
                await Wait(() => (dialog = window.OwnedWindows.OfType<OfficeRephraseWindow>().FirstOrDefault()) != null);
                var preset = Find<ComboBox>(dialog!).Single();
                Assert.AreEqual(6, preset.Items.Count);
                preset.SelectedIndex = 5;
                var instruction = Find<TextBox>(dialog!).Single(t => AutomationProperties.GetAutomationId(t) == "Office.Rephrase.Instruction");
                Assert.AreEqual("", instruction.Text);
                instruction.Text = "Сделай текст официальным и вежливым. Сохрани срок и время.";
                Click(dialog!, "Generate");
                await Wait(() => Find<System.Windows.Shapes.Path>(dialog!).Any(p => p.IsVisible));
                await Wait(() => Button(dialog!, "Apply").IsEnabled, 150);
                var result = Find<TextBox>(dialog!).Single(t => AutomationProperties.GetAutomationId(t) == "Office.Rephrase.Result");
                Assert.IsTrue(result.Text.Length > 10 && result.Text.Contains("10"));
                StringAssert.Contains(result.Text.ToLowerInvariant(), "завтра");
                StringAssert.Contains(result.Text.ToLowerInvariant(), "документ");
                Assert.IsFalse(result.Text.Any(c => "іїєґІЇЄҐ".Contains(c)), "The Russian source must stay in Russian.");
                var work = (OfficeTextWork)typeof(OfficeTextEditorView).GetField("_work", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(view)!;
                var entry = work.AiEntries.Last(e => e.Status == "completed");
                Assert.IsTrue(entry.Models.Any(m => m.Role == "text rephrasing"));
                using (var request = JsonDocument.Parse(entry.Request))
                    Assert.AreEqual(instruction.Text, request.RootElement.GetProperty("RequestedStyle").GetString());
                StringAssert.Contains(entry.Request, sentence);
                File.WriteAllText(Path.Combine(proof, "inference.json"), JsonSerializer.Serialize(new { entry.Request, entry.SystemPrompt, entry.Response, entry.Models }));
                File.WriteAllText(Path.Combine(proof, "model-result.txt"), result.Text);
                Capture(dialog!, Path.Combine(proof, "preview.png"));
                result.Text = "Документы будут направлены завтра в 10 часов.";
                Click(dialog!, "Apply"); await Wait(() => !dialog!.IsVisible);
                var updated = (await browser.RequestAsync(new { action = "text", selection = false }, default)).GetString()!;
                StringAssert.Contains(updated, result.Text); StringAssert.Contains(updated, "Оставить эту строку прежней.");
                await browser.RequestAsync(new { action = "command", name = "Undo" }, default);
                StringAssert.Contains((await browser.RequestAsync(new { action = "text", selection = false }, default)).GetString()!, sentence);
                await browser.RequestAsync(new { action = "find", text = sentence }, default);
                var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => browser.RequestAsync(new
                { action = "correct", original = sentence, stamp = context.GetProperty("stamp").GetString(), text = "wrong" }, default));
                Assert.AreEqual("OfficeEditor.SelectionChanged", error.Message);
                await Menu("rephrase"); await Wait(() => window.OwnedWindows.OfType<OfficeRephraseWindow>().Any());
                dialog = window.OwnedWindows.OfType<OfficeRephraseWindow>().Single();
                Click(dialog, "Cancel"); await Wait(() => !dialog.IsVisible);
                CollectionAssert.AreEqual(initial, File.ReadAllBytes(source));
            }
            finally
            {
                dialog?.Close();
                await Wait(() => !window.OwnedWindows.OfType<OfficeRephraseWindow>().Any());
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
                timer.Tick += (_, _) =>
                {
                    foreach (Window popup in window.OwnedWindows)
                        Find<Button>(popup).FirstOrDefault(b => AutomationProperties.GetAutomationId(b) == "Office.Editor.Close.Discard")
                            ?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                };
                timer.Start();
                try { await window.CloseEditorAsync(); }
                finally { timer.Stop(); folder.Delete(true); }
            }
        });
    }

    private static Button Button(DependencyObject parent, string id) => Find<Button>(parent).Single(b => AutomationProperties.GetAutomationId(b) == "Office.Rephrase." + id);
    private static void Click(DependencyObject parent, string id) => Button(parent, id).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
    private static async Task Wait(Func<bool> predicate, int seconds = 45)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (!predicate()) { if (DateTime.UtcNow > end) Assert.Fail("Rephrase UI did not reach the expected state."); await Task.Delay(60); }
    }
    private static IEnumerable<T> Find<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in Find<T>(child)) yield return nested;
        }
    }
    private static void Capture(FrameworkElement visual, string file)
    {
        var bitmap = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(file); png.Save(stream);
    }
}
