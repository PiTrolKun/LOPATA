using System.IO;
using System.Windows;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIHub.Controls.OfficeReader;
using AIHub.Controls.OfficeTextEditor;
using AIHub.Services;
using AIHub.Services.OfficeEditor;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class OfficeSymbolsUiTests
{
    [TestMethod, DataRow(true), DataRow(false)]
    public Task ToolbarOpensReusablePaletteAndCopiesUnicodeExactly(bool dark)
    {
        var evidence = Environment.GetEnvironmentVariable("LOPATA_SYMBOLS_UI");
        if (string.IsNullOrWhiteSpace(evidence)) Assert.Inconclusive("Opt in to WPF windows and Windows clipboard.");
        return OfficeProjectUiThread.RunAsync(async () =>
        {
            Directory.CreateDirectory(evidence);
            var previousClipboard = System.Windows.Clipboard.GetDataObject();
            var folder = Directory.CreateTempSubdirectory("lopata-symbols-");
            var path = Path.Combine(folder.FullName, "source.docx");
            var original = OfficeTextFormats.PlainDocument("Original document."); File.WriteAllBytes(path, original);
            var loc = new LocalizationService(); loc.Load(dark ? "ru" : "en");
            var window = new ReaderWindowGroup(null, loc.T, dark).OpenEditor(path);
            OfficeSymbolsWindow? palette = null;
            try
            {
                await Wait(() => Find<Button>(window).Any(b => AutomationProperties.GetAutomationId(b) == "Office.Editor.Symbols" && b.IsEnabled));
                var launch = Find<Button>(window).Single(b => AutomationProperties.GetAutomationId(b) == "Office.Editor.Symbols");
                Assert.AreEqual("±😀", launch.Content);
                launch.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                await Wait(() => (palette = window.OwnedWindows.OfType<OfficeSymbolsWindow>().SingleOrDefault()) != null);
                Assert.IsTrue(window.IsEnabled, "The palette must allow continued work in the editor.");
                var search = Find<TextBox>(palette!).Single(t => AutomationProperties.GetAutomationId(t) == "Office.Symbols.Search");
                await Wait(() => search.IsEnabled);
                var list = Find<OfficeSymbolsBrowser>(palette!).Single();
                await Wait(() => list.EntryCount > 170000);
                await ScriptWait(list, "document.querySelectorAll('#rows button').length>0");
                Assert.AreEqual("true", await list.CoreWebView2.ExecuteScriptAsync("document.querySelectorAll('#rows button').length<200"), "The whole catalogue must stay virtualized.");
                await list.CoreWebView2.ExecuteScriptAsync("const view=document.getElementById('viewport');view.scrollTop=view.scrollHeight");
                await ScriptWait(list, "[...document.querySelectorAll('#rows button')].some(b=>Number(b.dataset.index)===Number(document.body.dataset.count)-1)");
                var font = Find<ComboBox>(palette!).Single(c => AutomationProperties.GetAutomationId(c) == "Office.Symbols.Font");
                font.SelectedItem = "Arial";
                await ScriptWait(list, "getComputedStyle(document.documentElement).getPropertyValue('--glyph-font').includes('Arial')");
                font.SelectedIndex = 0;
                await ScriptWait(list, "!getComputedStyle(document.documentElement).getPropertyValue('--glyph-font').includes('Arial')");
                var selection = Find<TextBox>(palette!).Single(t => AutomationProperties.GetAutomationId(t) == "Office.Symbols.Selection");
                async Task Add(string code, string symbol)
                {
                    search.Text = code;
                    var value = JsonSerializer.Serialize(symbol);
                    await ScriptWait(list, "document.body.dataset.count==='1' && document.querySelector('#rows button')?.dataset.symbol===" + value);
                    var expected = selection.Text + symbol;
                    await list.CoreWebView2.ExecuteScriptAsync("document.querySelector('#rows button').click()");
                    await Wait(() => selection.Text == expected);
                }
                await Add("U+1F600", "😀"); await Add("U+2764 U+FE0F", "❤️"); await Add("U+1F3AE", "🎮");
                await Add("U+21D2", "⇒"); await Add("U+00B1", "±"); await Add("U+20BD", "₽"); await Add("U+2551", "║");
                await Add("U+1F93D U+1F3FB U+200D U+2642 U+FE0F", "🤽🏻‍♂️");
                await Add("U+1F93D U+1F3FF U+200D U+2642 U+FE0F", "🤽🏿‍♂️");
                const string combined = "😀❤️🎮⇒±₽║🤽🏻‍♂️🤽🏿‍♂️";
                Assert.AreEqual(combined, selection.Text);
                void Press(string id) => Find<Button>(palette!).Single(b => AutomationProperties.GetAutomationId(b) == "Office.Symbols." + id)
                    .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Press("Copy"); Assert.AreEqual(combined, System.Windows.Clipboard.GetText());
                selection.Select(2, 2); Press("Copy"); Assert.AreEqual("❤️", System.Windows.Clipboard.GetText());
                launch.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Assert.AreEqual(1, window.OwnedWindows.OfType<OfficeSymbolsWindow>().Count());
                Assert.AreEqual(combined, selection.Text);
                search.Text = "water polo";
                await ScriptWait(list, "document.body.dataset.count!=='1' && [...document.querySelectorAll('#rows button')].some(b=>b.dataset.symbol==='🤽🏻‍♂️') && [...document.querySelectorAll('#rows button')].some(b=>b.dataset.symbol==='🤽🏿‍♂️')");
                await ScriptWait(list, "document.getElementById('selected').textContent===" + JsonSerializer.Serialize(combined));
                palette!.UpdateLayout();
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
                await Task.Delay(100);
                var imagePath = Path.Combine(evidence, dark ? "dark.png" : "light.png");
                await using (var stream = File.Create(imagePath)) await list.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, stream);
                await CheckColorTones(list, imagePath);
                Press("Clear"); Assert.AreEqual("", selection.Text);
                Assert.IsFalse(Find<Button>(palette).Single(b => AutomationProperties.GetAutomationId(b) == "Office.Symbols.Copy").IsEnabled);
                Press("Close");
                await Wait(() => !window.OwnedWindows.OfType<OfficeSymbolsWindow>().Any());
                var browser = Find<OfficeTextBrowser>(window).Single(b => b.IsVisible);
                Assert.AreEqual("Original document.", (await browser.RequestAsync(new { action = "text", selection = false }, default)).GetString()!.Trim());
                CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
            }
            finally
            {
                palette?.Close();
                try { await window.CloseEditorAsync(); }
                finally
                {
                    folder.Delete(true);
                    if (previousClipboard != null) System.Windows.Clipboard.SetDataObject(previousClipboard, true);
                    else System.Windows.Clipboard.Clear();
                }
            }
        });
    }

    private static async Task CheckColorTones(OfficeSymbolsBrowser browser, string path)
    {
        var json = await browser.CoreWebView2.ExecuteScriptAsync("JSON.stringify({width:innerWidth,rects:['🤽🏻‍♂️','🤽🏿‍♂️'].map(s=>[...document.querySelectorAll('#rows button')].find(b=>b.dataset.symbol===s).querySelector('.glyph').getBoundingClientRect().toJSON())})");
        using var geometry = JsonDocument.Parse(JsonSerializer.Deserialize<string>(json)!);
        using var bitmap = SkiaSharp.SKBitmap.Decode(path);
        var scale = bitmap.Width / geometry.RootElement.GetProperty("width").GetDouble();
        var brightness = new List<double>();
        var counts = new List<int>();
        foreach (var rect in geometry.RootElement.GetProperty("rects").EnumerateArray())
        {
            var pixels = new List<double>();
            var left = (int)(rect.GetProperty("left").GetDouble() * scale); var top = (int)(rect.GetProperty("top").GetDouble() * scale);
            var right = (int)(rect.GetProperty("right").GetDouble() * scale); var bottom = (int)(rect.GetProperty("bottom").GetDouble() * scale);
            for (var y = top; y < bottom; y++) for (var x = left; x < right; x++)
            {
                var color = bitmap.GetPixel(x, y);
                if (Math.Max(color.Red, Math.Max(color.Green, color.Blue)) - Math.Min(color.Red, Math.Min(color.Green, color.Blue)) > 45)
                    pixels.Add(.2126 * color.Red + .7152 * color.Green + .0722 * color.Blue);
            }
            Assert.IsTrue(pixels.Count > 100, "The displayed glyph must contain actual color pixels.");
            counts.Add(pixels.Count);
            brightness.Add(pixels.Average());
        }
        Assert.IsTrue(brightness[0] > brightness[1] + 4, $"The light and dark skin tones must visibly differ: {brightness[0]}, {brightness[1]}.");
        File.WriteAllText(Path.ChangeExtension(path, ".metrics.json"), JsonSerializer.Serialize(new { counts, brightness }));
    }
    private static async Task ScriptWait(OfficeSymbolsBrowser browser, string script)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (await browser.CoreWebView2.ExecuteScriptAsync(script) != "true")
        { if (DateTime.UtcNow > deadline) Assert.Fail("The color palette did not update: " + script); await Task.Delay(60); }
    }
    private static async Task Wait(Func<bool> predicate)
    {
        var end = DateTime.UtcNow.AddSeconds(45);
        while (!predicate()) { if (DateTime.UtcNow > end) Assert.Fail("Special character palette did not reach the expected state."); await Task.Delay(60); }
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
