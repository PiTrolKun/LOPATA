using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Controls.OfficeReader;
using AIHub.Services;
using AIHub.Services.OfficeEditor;
using AIHub.Services.OfficeReader;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class OfficeNativeSectionWindowTests
{
    [TestMethod, TestCategory("Native")]
    [DataRow("04_presentations/RU/complex_8_slides.pptx")]
    [DataRow("04_presentations/RU/complex_8_slides.odp")]
    [DataRow("06_ebooks/RU/epub3_chapters_image.epub")]
    [DataRow("06_ebooks/RU/fb2_multichapter_image_notes.fb2")]
    public async Task ImportedParagraphCanBeEditedAndReopenedInTheSameNativeFormat(string fixture)
    {
        if (Environment.GetEnvironmentVariable("LOPATA_NATIVE_SECTION_UI") != "1")
            Assert.Inconclusive("Set LOPATA_NATIVE_SECTION_UI=1 to exercise the native editor with local office fixtures.");
        var source = Path.Combine(AppDataPaths.ProjectRoot!, "Тесты", "OfficeReader_20261107", "user-suite",
            "AI_Office_TestSuite_RU_EN", fixture);
        await OfficeProjectUiThread.RunAsync(() => CheckWindow(source));
    }

    private static async Task CheckWindow(string source)
    {
        var sourceBytes = await File.ReadAllBytesAsync(source);
        var extension = Path.GetExtension(source);
        var original = OfficeNativeDocument.Open(sourceBytes, extension);
        var pageIndex = Enumerable.Range(1, original.Pages.Count - 1).First(i => original.Pages[i].Blocks.Any(b => b.Text.Length > 0));
        var blocks = original.Pages[pageIndex].Blocks;
        var blockIndex = Enumerable.Range(0, blocks.Count).First(i => blocks[i].Text.Length > 0);
        var block = blocks[blockIndex];
        var slide = extension is ".pptx" or ".odp";
        var wasBold = block.Bold;
        var wasItalic = block.Italic;
        var marker = "LopataNativeQA";
        var replacement = marker + " " + block.Text;
        var evidence = Environment.GetEnvironmentVariable("LOPATA_NATIVE_SECTION_EVIDENCE");
        var folder = string.IsNullOrWhiteSpace(evidence) ? Directory.CreateTempSubdirectory("lopata-native-window-").FullName :
            Directory.CreateDirectory(Path.Combine(evidence, extension[1..] + "-" + Guid.NewGuid().ToString("N"))).FullName;
        var copy = Path.Combine(folder, Path.GetFileName(source));
        await File.WriteAllBytesAsync(copy, sourceBytes);
        var loc = new LocalizationService(); loc.Load("ru");
        var group = new ReaderWindowGroup(null, loc.T, false);
        var oldGate = ComponentLicenseGate.ConfirmAsync;
        ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
        var window = group.OpenEditor(copy);
        try
        {
            var editor = await SelectBlock(window, pageIndex, blockIndex);
            Assert.AreEqual(block.Text.ReplaceLineEndings("\n"), editor.Text.ReplaceLineEndings("\n"));
            var handle = new WindowInteropHelper(window).Handle;
            editor.Focus(); editor.Select(0, 0); editor.SelectedText = marker + " ";
            if (slide)
            {
                Click(window, "Office.Native.Bold");
                Click(window, "Office.Native.Italic");
                Find<ComboBox>(window, "Office.Native.FontSize")!.SelectedItem = 24d;
                Assert.AreEqual(wasBold ? FontWeights.Normal : FontWeights.Bold, editor.FontWeight);
                Assert.AreEqual(wasItalic ? FontStyles.Normal : FontStyles.Italic, editor.FontStyle);
                Assert.AreEqual(32d, editor.FontSize);
            }
            if (!string.IsNullOrWhiteSpace(evidence)) CaptureWindow(window, Path.Combine(folder, "editor.png"));
            if (extension == ".pptx")
            {
                Click(window, "Office.Editor.Read");
                await Until(() => !window.IsEditing, 120);
                Assert.AreEqual(handle, new WindowInteropHelper(window).Handle);
                var reader = (ReaderDocument)typeof(ReaderWindow).GetField("_document",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                var result = await reader.FindAsync(marker, 0, CancellationToken.None);
                Assert.AreEqual(pageIndex, result.Page, "Read mode must render the edited slide before saving.");
                await window.SwitchToEditingAsync();
                Assert.AreEqual(handle, new WindowInteropHelper(window).Handle);
            }
            Click(window, "Office.Native.AddPage");
            await Until(() => Find<ListBox>(window, "Office.Native.Pages") is { IsEnabled: true, SelectedIndex: >= 0 } pages
                && pages.Items.Count == original.Pages.Count + 1);
            var addedPage = Find<ListBox>(window, "Office.Native.Pages")!.SelectedIndex;
            var addedText = await SelectBlock(window, addedPage, 0);
            addedText.Focus(); addedText.SelectAll(); addedText.SelectedText = "LopataAddedPage";
            CollectionAssert.AreEqual(sourceBytes, await File.ReadAllBytesAsync(copy));
            Click(window, "Office.Editor.Save");
            await Until(() => File.Exists(copy + ".bak") && Find<TextBox>(window, "Office.Native.Block.0")?.IsEnabled == true);
            var saved = OfficeNativeDocument.Open(await File.ReadAllBytesAsync(copy), extension);
            Assert.AreEqual(original.Pages.Count + 1, saved.Pages.Count);
            Assert.AreEqual("LopataAddedPage", saved.Pages[addedPage].Blocks[0].Text);
            var edited = saved.Pages[pageIndex].Blocks[blockIndex];
            Assert.AreEqual(replacement, edited.Text);
            if (slide)
            {
                Assert.AreEqual(!wasBold, edited.Bold);
                Assert.AreEqual(!wasItalic, edited.Italic);
                Assert.AreEqual(24d, edited.FontSize);
            }
            Assert.IsTrue(await window.CloseEditorAsync());
            window = group.OpenEditor(copy);
            var reopened = await SelectBlock(window, pageIndex, blockIndex);
            Assert.AreEqual(replacement.ReplaceLineEndings("\n"), reopened.Text.ReplaceLineEndings("\n"));
            var added = await SelectBlock(window, addedPage, 0);
            Assert.AreEqual("LopataAddedPage", added.Text);
            Assert.IsTrue(await window.CloseEditorAsync());
            CollectionAssert.AreEqual(sourceBytes, await File.ReadAllBytesAsync(copy + ".bak"));
            CollectionAssert.AreEqual(SHA256.HashData(sourceBytes), SHA256.HashData(await File.ReadAllBytesAsync(source)));
        }
        finally
        {
            if (window.IsVisible)
            {
                var discard = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                discard.Tick += (_, _) =>
                {
                    foreach (Window dialog in window.OwnedWindows)
                        Find<Button>(dialog, "Office.Editor.Close.Discard")?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                };
                discard.Start();
                try { await window.CloseEditorAsync(); } finally { discard.Stop(); }
            }
            ComponentLicenseGate.ConfirmAsync = oldGate;
            if (string.IsNullOrWhiteSpace(evidence))
            {
                File.Delete(copy); File.Delete(copy + ".bak");
                if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
            }
        }
    }

    private static async Task<TextBox> SelectBlock(Window window, int page, int block)
    {
        await Until(() => Find<ListBox>(window, "Office.Native.Pages")?.Items.Count > page);
        Find<ListBox>(window, "Office.Native.Pages")!.SelectedIndex = page;
        var paragraphs = Find<ListBox>(window, "Office.Native.Paragraphs")!;
        paragraphs.SelectedIndex = block;
        paragraphs.ScrollIntoView(paragraphs.Items[block]);
        window.UpdateLayout();
        await Until(() => Find<TextBox>(window, "Office.Native.Block." + block)?.IsVisible == true);
        return Find<TextBox>(window, "Office.Native.Block." + block)!;
    }

    private static T? Find<T>(DependencyObject element, string id) where T : DependencyObject
    {
        if (element is T value && AutomationProperties.GetAutomationId(element) == id) return value;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            if (Find<T>(VisualTreeHelper.GetChild(element, i), id) is { } child) return child;
        return null;
    }

    private static void Click(Window owner, string id)
    {
        var button = Find<Button>(owner, id);
        Assert.IsNotNull(button, id);
        Assert.IsTrue(button.IsEnabled, id);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static void CaptureWindow(Window window, string path)
    {
        var visual = (FrameworkElement)window.Content;
        visual.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth),
            (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
        png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        png.Save(output);
    }

    private static async Task Until(Func<bool> ready, int seconds = 35)
    {
        var timer = Stopwatch.StartNew();
        while (!ready())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(seconds)) throw new TimeoutException("The native material editor did not reach the expected state.");
            await Task.Delay(70);
        }
    }
}
