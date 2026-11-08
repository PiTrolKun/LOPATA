using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Controls.OfficeReader;
using AIHub.Controls.OfficeTextEditor;
using AIHub.Services;
using AIHub.Services.OfficeEditor;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class OfficeContextMenuTests
{
    [TestMethod, DataRow(".fb2"), DataRow(".pptx")]
    public Task NativeBookAndSlideTextKeepStandardCommands(string extension)
    {
        if (Environment.GetEnvironmentVariable("LOPATA_CONTEXT_MENU_UI") != "1") Assert.Inconclusive("Opt in to the native editor.");
        return OfficeProjectUiThread.RunAsync(async () =>
        {
            var directory = Directory.CreateTempSubdirectory("lopata-native-context-");
            var source = Path.Combine(directory.FullName, "source" + extension);
            File.WriteAllBytes(source, OfficeNativeDocument.Create(extension, [new() { Title = "Title", Body = "Native paragraph" }]));
            var localize = new LocalizationService(); localize.Load("ru");
            var window = new ReaderWindowGroup(null, localize.T, true).OpenEditor(source);
            System.Windows.Controls.ContextMenu? menu = null;
            try
            {
                await Wait(() => Find<TextBox>(window).Any(t => t.Text == "Native paragraph" && t.IsVisible));
                var field = Find<TextBox>(window).First(t => t.Text == "Native paragraph" && t.IsVisible);
                field.Focus(); field.Select(0, 6);
                var args = (ContextMenuEventArgs)Activator.CreateInstance(typeof(ContextMenuEventArgs),
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                    null, [field, true], null)!;
                args.RoutedEvent = FrameworkElement.ContextMenuOpeningEvent; field.RaiseEvent(args);
                await Wait(() => (menu = PresentationSource.CurrentSources.Cast<PresentationSource>()
                    .Where(s => s.RootVisual != null).SelectMany(s => Find<System.Windows.Controls.ContextMenu>(s.RootVisual))
                    .FirstOrDefault(m => m.IsOpen && ReferenceEquals(m.PlacementTarget, field))) != null);
                foreach (var label in new[] { "Отменить", "Повторить", "Вырезать", "Копировать", "Вставить", "Удалить", "Выделить всё", "Сделать переформулировку", "Найти в интернете" })
                    Assert.IsTrue(menu!.Items.OfType<MenuItem>().Any(i => Equals(i.Header, label)), label);
                menu!.IsOpen = false;
                field.SelectedText = "Edited";
                var undo = menu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Отменить"));
                ((System.Windows.Input.RoutedCommand)undo.Command).Execute(null, field);
                Assert.AreEqual("Native paragraph", field.Text);
                var redo = menu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Повторить"));
                ((System.Windows.Input.RoutedCommand)redo.Command).Execute(null, field);
                Assert.AreEqual("Edited paragraph", field.Text);
            }
            finally
            {
                if (menu != null) menu.IsOpen = false;
                var discard = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
                discard.Tick += (_, _) =>
                {
                    foreach (Window dialog in window.OwnedWindows)
                        Find<Button>(dialog).FirstOrDefault(b => AutomationProperties.GetAutomationId(b) == "Office.Editor.Close.Discard")?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                };
                discard.Start();
                try { await window.CloseEditorAsync(); }
                finally { discard.Stop(); directory.Delete(true); }
            }
        });
    }

    [TestMethod]
    public Task StandardClipboardCommandsRemainAlongsideSelectionActions()
    {
        if (Environment.GetEnvironmentVariable("LOPATA_CONTEXT_MENU_UI") != "1") Assert.Inconclusive("Opt in to the real editor and Windows clipboard.");
        return OfficeProjectUiThread.RunAsync(async () =>
        {
            var previousClipboard = System.Windows.Clipboard.GetDataObject();
            var directory = Directory.CreateTempSubdirectory("lopata-context-menu-");
            var source = Path.Combine(directory.FullName, "source.docx");
            const string phrase = "Copy this part.";
            const string keep = "Keep this line.";
            var bytes = OfficeTextFormats.PlainDocument(phrase + "\n" + keep); File.WriteAllBytes(source, bytes);
            var localize = new LocalizationService(); localize.Load("ru");
            var window = new ReaderWindowGroup(null, localize.T, true).OpenEditor(source);
            try
            {
                await Wait(() => Find<Button>(window).Any(b => AutomationProperties.GetAutomationId(b) == "Office.Editor.Summary" && b.IsEnabled));
                var browser = Find<OfficeTextBrowser>(window).Single(b => b.IsVisible);
                async Task Select(string text) => await browser.RequestAsync(new { action = "find", text }, default);
                async Task<string> Text(bool selection = false) => (await browser.RequestAsync(new { action = "text", selection }, default)).GetString() ?? "";
                async Task Menu(string? command = null)
                {
                    var count = await browser.CoreWebView2.ExecuteScriptAsync("document.querySelector('#viewport').dispatchEvent(new MouseEvent('contextmenu',{bubbles:true,cancelable:true,clientX:200,clientY:200})); document.querySelectorAll('.lopata-spelling-menu [data-command]').length");
                    Assert.AreEqual("8", count);
                    Assert.AreEqual("2", await browser.CoreWebView2.ExecuteScriptAsync("document.querySelectorAll('.lopata-spelling-menu [data-action]').length"));
                    if (command != null)
                        await browser.CoreWebView2.ExecuteScriptAsync("document.querySelector('[data-command=" + command + "]').click()");
                }
                async Task UntilText(Func<string, bool> predicate, bool selection = false)
                {
                    var end = DateTime.UtcNow.AddSeconds(10);
                    while (!predicate(await Text(selection))) { if (DateTime.UtcNow > end) Assert.Fail("Context command did not update the document."); await Task.Delay(50); }
                }
                await Select(phrase); await Menu("Copy");
                await Wait(() => System.Windows.Clipboard.ContainsText() && System.Windows.Clipboard.GetText() == phrase);
                Assert.IsTrue(System.Windows.Clipboard.ContainsData(DataFormats.Html));
                await Menu("Cut"); await UntilText(t => !t.Contains(phrase) && t.Contains(keep));
                await Select(keep); await Menu("Undo"); await UntilText(t => t.Contains(phrase));
                await Select(phrase); await Menu("Redo"); await UntilText(t => !t.Contains(phrase));
                await Select(keep); await Menu("Undo"); await UntilText(t => t.Contains(phrase));
                await Select(keep); await Menu("Paste"); await UntilText(t => t.Split(phrase).Length == 3 && !t.Contains(keep));
                await Select(phrase); await Menu("Undo"); await UntilText(t => t.Contains(keep));
                System.Windows.Clipboard.SetText("Plain paste.");
                await Select(phrase); await Menu("PastePlain"); await UntilText(t => t.Contains("Plain paste.") && t.Contains(keep));
                await Select("Plain paste."); await Menu("Undo"); await UntilText(t => t.Contains(phrase));
                await Select(phrase); await Menu("Delete"); await UntilText(t => !t.Contains(phrase) && t.Contains(keep));
                await Select(keep); await Menu("Undo"); await UntilText(t => t.Contains(phrase));
                await Select(phrase); await Menu("SelectAll");
                await UntilText(t => t.Contains(phrase) && t.Contains(keep), true);
                var selected = await Text(true); StringAssert.Contains(selected, phrase); StringAssert.Contains(selected, keep);
                CollectionAssert.AreEqual(bytes, File.ReadAllBytes(source));
            }
            finally
            {
                var discard = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
                discard.Tick += (_, _) =>
                {
                    foreach (Window dialog in window.OwnedWindows)
                        Find<Button>(dialog).FirstOrDefault(b => AutomationProperties.GetAutomationId(b) == "Office.Editor.Close.Discard")?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                };
                discard.Start();
                try { await window.CloseEditorAsync(); }
                finally
                {
                    discard.Stop(); directory.Delete(true);
                    if (previousClipboard != null) System.Windows.Clipboard.SetDataObject(previousClipboard, true);
                }
            }
        });
    }

    private static async Task Wait(Func<bool> predicate)
    {
        var end = DateTime.UtcNow.AddSeconds(45);
        while (!predicate()) { if (DateTime.UtcNow > end) Assert.Fail("Editor menu did not reach the expected state."); await Task.Delay(60); }
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
}
