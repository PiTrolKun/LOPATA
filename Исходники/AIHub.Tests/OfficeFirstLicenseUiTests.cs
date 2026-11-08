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
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.OfficeEditor;
using AIHub.Services.OfficeReader;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class OfficeFirstLicenseUiTests
{
    [TestMethod, DataRow("ru", true), DataRow("en", false)]
    public Task ReaderOpensEditorWithFreshTermsWhileMainWindowIsHidden(string language, bool acceptUnicode) => OfficeProjectUiThread.RunAsync(async () =>
    {
        var evidence = Environment.GetEnvironmentVariable("LOPATA_OFFICE_LICENSE_UI");
        if (string.IsNullOrWhiteSpace(evidence)) Assert.Inconclusive("Opt in to the real reader, editor and license dialogs.");
        var folder = Directory.CreateTempSubdirectory("lopata-first-license-");
        var path = Path.Combine(folder.FullName, "first-use.docx");
        var bytes = OfficeTextFormats.PlainDocument("First use. Солнце sun."); File.WriteAllBytes(path, bytes);
        // Match the reported cache-hit reading path without re-testing the external converter.
        using (var pdf = new UglyToad.PdfPig.Writer.PdfDocumentBuilder())
        {
            pdf.AddPage(600, 800);
            var preview = Path.Combine(folder.FullName, "preview.pdf"); File.WriteAllBytes(preview, pdf.Build());
            await ReaderPreviewCache.StoreAsync(await ReaderPreviewCache.KeyAsync(path, ".docx", default), preview, default);
        }
        var service = new ComponentLicenseService(Path.Combine(AppContext.BaseDirectory, "Licenses"), Path.Combine(folder.FullName, "receipts.json"));
        await service.EnsureAsync(["nuget.PdfPig", "nuget.SharpCompress", "nuget.SkiaSharp", "nuget.SkiaSharp.NativeAssets.Win32",
            "runtime.libreoffice", "viewer.webview2", "nuget.Microsoft.Web.WebView2"], _ => Task.FromResult(true), default);
        var loc = new LocalizationService(); loc.Load(language);
        var hidden = new Window { ShowInTaskbar = false }; hidden.Show(); hidden.Hide();
        var reader = new ReaderWindowGroup(hidden, loc.T, language == "ru").Open();
        var gate = ComponentLicenseGate.ConfirmAsync; var controller = ApplicationBackgroundOperations.Current;
        ApplicationBackgroundOperations.Current = new(new BackgroundOperationStore(Path.Combine(folder.FullName, "background.json")));
        var prompts = new List<string[]>(); var owners = new List<bool>(); var retryUnicode = acceptUnicode;
        Exception? dialogError = null;
        ComponentLicenseGate.ConfirmAsync = (ids, token) => service.EnsureAsync(ComponentLicenseService.ExpandSelection(ids, service.Entries),
            entries => reader.Dispatcher.InvokeAsync(() => MainWindow.ShowComponentLicenses(hidden, Windows(reader).Prepend(hidden),
                service, loc.T, language == "ru", entries, true)).Task, token);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        timer.Tick += (_, _) =>
        {
            foreach (var dialog in Windows(reader).Where(w => w.Title == loc.T("licenses.title") && w.IsVisible).ToArray())
            {
                try
                {
                    var ids = Find<ListBox>(dialog).Single().Items.Cast<ComponentLicenseEntry>().Select(e => e.Id).ToArray();
                    prompts.Add(ids); owners.Add(OwnedBy(dialog, reader) && !hidden.IsVisible);
                    if (ids.Contains(OfficeUnicodeCatalog.Component)) Capture(dialog, Path.Combine(evidence, "license-" + language + ".png"));
                    var key = ids.Contains(OfficeUnicodeCatalog.Component) && !retryUnicode ? "licenses.cancel" : "licenses.accept";
                    Find<Button>(dialog).Single(b => Equals(b.Content, loc.T(key))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                catch (Exception error) { dialogError = error; dialog.Close(); }
            }
        };
        timer.Start();
        try
        {
            await reader.OpenAsync(path).WaitAsync(TimeSpan.FromSeconds(40));
            Assert.IsTrue(Find<Button>(reader).Single(b => AutomationProperties.GetAutomationId(b) == "Office.Reader.Editor").IsEnabled);
            reader.Activate();
            Find<Button>(reader).Single(b => AutomationProperties.GetAutomationId(b) == "Office.Reader.Editor").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => Find<Button>(reader).Any(b => AutomationProperties.GetAutomationId(b) == "Office.Editor.Save" && b.IsEnabled));
            var browser = Find<OfficeTextBrowser>(reader).Single(b => b.IsVisible);
            StringAssert.Contains((await browser.RequestAsync(new { action = "text", selection = false }, default)).GetString()!, "First use.");
            Assert.IsTrue(reader.IsEditing); Assert.IsFalse(hidden.IsVisible);
            Assert.AreEqual(acceptUnicode, service.ReadReceipts().Any(r => r.Id == OfficeUnicodeCatalog.Component));
            retryUnicode = true;
            Find<Button>(reader).Single(b => AutomationProperties.GetAutomationId(b) == "Office.Editor.Symbols").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => reader.OwnedWindows.OfType<OfficeSymbolsWindow>().Any(w => Find<TextBox>(w).Any(t => AutomationProperties.GetAutomationId(t) == "Office.Symbols.Search" && t.IsEnabled)));
            reader.OwnedWindows.OfType<OfficeSymbolsWindow>().Single().Close();
            var reopened = new ComponentLicenseService(Path.Combine(AppContext.BaseDirectory, "Licenses"), Path.Combine(folder.FullName, "receipts.json"));
            await reopened.EnsureAsync([OfficeUnicodeCatalog.Component, "office.text-surface"], _ => throw new AssertFailedException("Accepted terms must persist."), default);
            Assert.IsNull(dialogError); Assert.IsTrue(owners.All(x => x));
            Assert.AreEqual(acceptUnicode ? 1 : 2, prompts.Count(p => p.Contains(OfficeUnicodeCatalog.Component)));
            Assert.AreEqual(1, prompts.Count(p => p.Contains("office.text-surface")));
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
            File.WriteAllText(Path.Combine(evidence, "first-use-" + language + ".json"), JsonSerializer.Serialize(new
                { language, acceptUnicode, prompts, owners, editorReady = reader.IsEditing, mainHidden = !hidden.IsVisible, receiptsPersisted = true }));
        }
        finally
        {
            timer.Stop();
            try { await reader.CloseEditorAsync(); }
            finally
            {
                hidden.Close(); ComponentLicenseGate.ConfirmAsync = gate; ApplicationBackgroundOperations.Current = controller;
                folder.Delete(true);
            }
        }
    });

    [TestMethod]
    public Task HiddenMainStillBlocksUnrelatedOrMixedTerms() => OfficeProjectUiThread.RunAsync(() =>
    {
        var folder = Directory.CreateTempSubdirectory("lopata-license-owner-");
        var hidden = new Window(); hidden.Show(); hidden.Hide();
        var reader = new ReaderWindow(hidden, x => x, false);
        var controller = ApplicationBackgroundOperations.Current;
        ApplicationBackgroundOperations.Current = new(new BackgroundOperationStore(Path.Combine(folder.FullName, "background.json")));
        try
        {
            reader.Show(); reader.Activate();
            var service = new ComponentLicenseService(Path.Combine(AppContext.BaseDirectory, "Licenses"), Path.Combine(folder.FullName, "receipts.json"));
            foreach (var ids in new[] { new[] { "generation-krea" }, new[] { OfficeUnicodeCatalog.Component, "generation-krea" } })
                Assert.Throws<BackgroundOperationWaitingException>(() => MainWindow.ShowComponentLicenses(hidden, [hidden, reader], service,
                    x => x, false, ids.Select(id => service.Entries.Single(e => e.Id == id)).ToArray(), true));
        }
        finally { reader.Close(); hidden.Close(); ApplicationBackgroundOperations.Current = controller; folder.Delete(true); }
        return Task.CompletedTask;
    });

    private static IEnumerable<Window> Windows(Window owner)
    {
        yield return owner;
        foreach (Window child in owner.OwnedWindows)
            foreach (var window in Windows(child)) yield return window;
    }
    private static bool OwnedBy(Window window, Window ancestor)
    {
        for (var owner = window.Owner; owner != null; owner = owner.Owner) if (ReferenceEquals(owner, ancestor)) return true;
        return false;
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
    private static async Task Wait(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!predicate()) { if (DateTime.UtcNow > deadline) Assert.Fail("The editor did not become ready after the license decision."); await Task.Delay(80); }
    }
    private static void Capture(Window window, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); png.Save(file);
    }
}
