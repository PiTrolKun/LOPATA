using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AIHub.Controls.OfficeReader;
using AIHub.Controls.OfficeTextEditor;
using AIHub.Services;
using AIHub.Services.OfficeEditor;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class OfficeSymbolTypingUiTests
{
    [TestMethod]
    public Task BookTypingOffersEmojiAndKeepsUndo()
    {
        var evidence = Environment.GetEnvironmentVariable("LOPATA_SYMBOL_TYPING_UI");
        if (string.IsNullOrWhiteSpace(evidence)) Assert.Inconclusive("Opt in to the real typing UI.");
        return OfficeProjectUiThread.RunAsync(async () =>
        {
            var folder = Directory.CreateTempSubdirectory("lopata-symbol-book-");
            var source = Path.Combine(folder.FullName, "input.fb2");
            File.WriteAllBytes(source, OfficeNativeDocument.Create(".fb2", [new() { Title = "Title", Body = "Солнц" }]));
            var loc = new LocalizationService(); loc.Load("ru");
            var window = new ReaderWindowGroup(null, loc.T, true).OpenEditor(source);
            try
            {
                await Wait(() => Find<System.Windows.Controls.TextBox>(window).Any(t => t.IsVisible && t.Text == "Солнц"));
                var field = Find<System.Windows.Controls.TextBox>(window).Single(t => t.IsVisible && t.Text == "Солнц");
                window.Activate(); field.Focus(); field.CaretIndex = field.Text.Length;
                var input = new TextComposition(InputManager.Current, field, "е");
                field.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, input) { RoutedEvent = TextCompositionManager.TextInputEvent });
                await Wait(() => field.Text == "Солнце");
                var view = Find<OfficeTextEditorView>(window).Single();
                var popup = (Popup)typeof(OfficeTextEditorView).GetField("_nativeSymbols", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(view)!;
                await Wait(() => popup.IsOpen);
                var choice = Find<System.Windows.Controls.Button>(popup.Child).First();
                var symbol = (string)choice.Content;
                Assert.AreEqual("Солнце", field.Text);
                choice.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Assert.AreEqual(symbol, field.Text);
                Assert.IsFalse(popup.IsOpen);
                ApplicationCommands.Undo.Execute(null, field);
                Assert.AreEqual("Солнце", field.Text);
            }
            finally
            {
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
                timer.Tick += (_, _) =>
                {
                    foreach (Window dialog in window.OwnedWindows)
                        Find<System.Windows.Controls.Button>(dialog).FirstOrDefault(b => AutomationProperties.GetAutomationId(b) == "Office.Editor.Close.Discard")?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                };
                timer.Start();
                try { await window.CloseEditorAsync(); }
                finally { timer.Stop(); folder.Delete(true); }
            }
        });
    }

    [TestMethod]
    public Task ManualInputSuggestsRussianEnglishCurrenciesAndFacesWithoutAutomaticReplacement()
    {
        var evidence = Environment.GetEnvironmentVariable("LOPATA_SYMBOL_TYPING_UI");
        if (string.IsNullOrWhiteSpace(evidence)) Assert.Inconclusive("Opt in to the real typing UI.");
        return OfficeProjectUiThread.RunAsync(async () =>
        {
            Directory.CreateDirectory(evidence);
            var folder = Directory.CreateTempSubdirectory("lopata-symbol-typing-");
            var source = Path.Combine(folder.FullName, "input.docx"); File.WriteAllBytes(source, OfficeTextFormats.PlainDocument("Start"));
            var loc = new LocalizationService(); loc.Load("ru");
            var window = new ReaderWindowGroup(null, loc.T, true).OpenEditor(source);
            try
            {
                await Wait(() => Find<Button>(window).Any(b => AutomationProperties.GetAutomationId(b) == "Office.Editor.Symbols" && b.IsEnabled));
                var browser = Find<OfficeTextBrowser>(window).Single(b => b.IsVisible);
                async Task<string> Text() => (await browser.RequestAsync(new { action = "text", selection = false }, default)).GetString()!.Trim();
                async Task<bool> ScriptTrue(string script) => await browser.CoreWebView2.ExecuteScriptAsync(script) == "true";
                foreach (var word in new[] { "Солнце", "sun", "рубль", "dollar", "=)" })
                {
                    await browser.RequestAsync(new { action = "command", name = "SelectAll" }, default);
                    await browser.RequestAsync(new { action = "paste", text = word[..^1] }, default);
                    window.Activate(); browser.Focus();
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Input);
                    var tail = JsonSerializer.Serialize(word[^1..]);
                    Assert.IsTrue(await ScriptTrue("(()=>{const input=document.querySelector('[contenteditable=true]'); if(!input)return false;input.focus();input.dispatchEvent(new InputEvent('beforeinput',{inputType:'insertText',data:" + tail + ",bubbles:true,cancelable:true}));return true})()"));
                    var deadline = DateTime.UtcNow.AddSeconds(10);
                    while (await Text() != word) { if (DateTime.UtcNow > deadline) Assert.Fail("The input event did not type the expected word: " + await Text()); await Task.Delay(70); }
                    while (!await ScriptTrue("!!document.querySelector('.lopata-symbol-suggestions button')"))
                    { if (DateTime.UtcNow > deadline) Assert.Fail("No symbol suggestions for " + word); await Task.Delay(70); }
                    Assert.AreEqual(word, await Text(), "Showing suggestions must leave the typed word intact.");
                    var placement = await browser.CoreWebView2.ExecuteScriptAsync("(()=>{const menu=document.querySelector('.lopata-symbol-suggestions');return JSON.stringify({rect:menu.getBoundingClientRect().toJSON(),style:getComputedStyle(menu).cssText,top:menu.style.top,left:menu.style.left,width:innerWidth,height:innerHeight,body:document.body.getBoundingClientRect().toJSON()})})()");
                    File.WriteAllText(Path.Combine(evidence, "typing-placement.json"), JsonSerializer.Deserialize<string>(placement)!);
                    Assert.IsTrue(await ScriptTrue("(()=>{const rect=document.querySelector('.lopata-symbol-suggestions').getBoundingClientRect();return rect.left>=0&&rect.top>=0&&rect.right<=innerWidth&&rect.bottom<=innerHeight})()"), "The complete suggestion row must fit in the viewport. " + placement);
                    if (word == "Солнце")
                    {
                        await using var screenshot = File.Create(Path.Combine(evidence, "typing-sun.png"));
                        await browser.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, screenshot);
                    }
                    var json = await browser.CoreWebView2.ExecuteScriptAsync("JSON.stringify([...document.querySelectorAll('[data-suggestion]')].map(b=>b.dataset.suggestion))");
                    var options = JsonSerializer.Deserialize<string[]>(JsonSerializer.Deserialize<string>(json)!)!;
                    var choice = word == "рубль" ? "₽" : word == "dollar" ? "$" : options[0];
                    CollectionAssert.Contains(options, choice);
                    var value = JsonSerializer.Serialize(choice);
                    if (word == "sun")
                        await browser.CoreWebView2.ExecuteScriptAsync("document.querySelector('[contenteditable=true]').dispatchEvent(new KeyboardEvent('keydown',{key:'Tab',bubbles:true,cancelable:true}))");
                    else if (word == "=)")
                        await browser.CoreWebView2.ExecuteScriptAsync("const input=document.querySelector('[contenteditable=true]');input.dispatchEvent(new KeyboardEvent('keydown',{key:'ArrowDown',bubbles:true,cancelable:true}));input.dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',bubbles:true,cancelable:true}))");
                    else
                        await browser.CoreWebView2.ExecuteScriptAsync("[...document.querySelectorAll('[data-suggestion]')].find(b=>b.dataset.suggestion===" + value + ").click()");
                    deadline = DateTime.UtcNow.AddSeconds(10);
                    while (await Text() != choice) { if (DateTime.UtcNow > deadline) Assert.Fail("The chosen symbol did not replace its word."); await Task.Delay(60); }
                    await browser.RequestAsync(new { action = "command", name = "Undo" }, default);
                    Assert.AreEqual(word, await Text());
                }
                await browser.CoreWebView2.ExecuteScriptAsync("const input=document.querySelector('[contenteditable=true]');input.dispatchEvent(new KeyboardEvent('keydown',{key:'ArrowRight',bubbles:true,cancelable:true}));input.dispatchEvent(new InputEvent('beforeinput',{inputType:'insertText',data:' ',bubbles:true,cancelable:true}))");
                await Task.Delay(150);
                Assert.IsTrue(await ScriptTrue("!document.querySelector('.lopata-symbol-suggestions')"));
                Assert.AreEqual("=)", await Text(), "Continuing ordinary input keeps the original emoticon.");
                File.WriteAllText(Path.Combine(evidence, "typing.json"), JsonSerializer.Serialize(new { words = new[] { "Солнце", "sun", "рубль", "dollar", "=)" }, explicitChoice = true, undo = true }));
            }
            finally
            {
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
                timer.Tick += (_, _) =>
                {
                    foreach (Window dialog in window.OwnedWindows)
                        Find<Button>(dialog).FirstOrDefault(b => AutomationProperties.GetAutomationId(b) == "Office.Editor.Close.Discard")?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                };
                timer.Start();
                try { await window.CloseEditorAsync(); }
                finally { timer.Stop(); folder.Delete(true); }
            }
        });
    }

    private static async Task Wait(Func<bool> predicate)
    {
        var end = DateTime.UtcNow.AddSeconds(45);
        while (!predicate()) { if (DateTime.UtcNow > end) Assert.Fail("Typing editor did not become ready."); await Task.Delay(60); }
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
