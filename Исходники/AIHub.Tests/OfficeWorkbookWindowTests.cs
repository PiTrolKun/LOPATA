using System.Diagnostics;
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
public sealed class OfficeWorkbookWindowTests
{
    [TestMethod, TestCategory("Native")]
    public async Task NativeTabsRemainInsideOneProjectSheetAfterCreationAndReopen()
    {
        var sample = Environment.GetEnvironmentVariable("LOPATA_WORKBOOK_WINDOW");
        if (string.IsNullOrEmpty(sample)) Assert.Inconclusive("Set LOPATA_WORKBOOK_WINDOW to a multiple-sheet XLSX sample.");
        await OfficeProjectUiThread.RunAsync(() => CheckWindow(sample));
    }

    private static async Task CheckWindow(string sample)
    {
        var folder = Directory.CreateTempSubdirectory("lopata-workbook-").FullName;
        var copy = Path.Combine(folder, "book-" + Guid.NewGuid().ToString("N") + ".xlsx"); File.Copy(sample, copy);
        var project = Path.Combine(folder, "проверка.LOPATAdoxAI");
        var loc = new LocalizationService(); loc.Load("ru");
        var original = new OfficeWorkbook(await File.ReadAllBytesAsync(copy), "Лист1");
        Assert.IsGreaterThanOrEqualTo(2, original.Sheets.Count);
        var second = original.Sheets[1].Name;
        var group = new ReaderWindowGroup(null, loc.T, false);
        var previousGate = ComponentLicenseGate.ConfirmAsync;
        ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
        var window = group.OpenEditor(copy);
        try
        {
            await Until(() => Find<OfficeWorkbookView>(window) != null);
            var ordinaryIcon = window.Icon;
            var strip = Find<DockPanel>(window, "Office.Editor.SheetStrip")!;
            Assert.AreEqual(Visibility.Collapsed, strip.Visibility);
            CaptureWindow(window, "xlsx-native-sheets");
            Click(window, "Office.Workbook.Tab." + second);
            var workbook = Find<OfficeWorkbookView>(window)!;
            var formula = Find<TextBox>(workbook, "Office.Workbook.Formula")!;
            formula.Text = "Изменение второго листа"; workbook.CommitEdit();
            Click(window, "Office.Editor.Save");
            await Until(() =>
            {
                try
                {
                    var saved = new OfficeWorkbook(File.ReadAllBytes(copy), "Лист1");
                    saved.Select(saved.Sheets[1]); return saved.CellAt(new(1, 1)).Input == formula.Text;
                }
                catch (IOException) { return false; }
            });
            Assert.AreEqual(Visibility.Collapsed, strip.Visibility);
            var create = Find<Menu>(window)!.Items.OfType<MenuItem>().SelectMany(root => root.Items.OfType<MenuItem>())
                .Single(item => AutomationProperties.GetAutomationId(item) == "Office.Editor.File.CreateProject");
            async Task SaveAsProject(string? destination)
            {
                var dialog = OfficeSheetWindowTests.CommonDialog(window, destination);
                create.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await dialog;
            }
            await SaveAsProject(null);
            Assert.AreEqual(Visibility.Collapsed, strip.Visibility);
            Assert.AreSame(ordinaryIcon, window.Icon);
            await SaveAsProject(project);
            await Until(() => strip.Visibility == Visibility.Visible && File.Exists(project));
            Assert.IsNotNull(window.Icon);
            Assert.AreNotSame(ordinaryIcon, window.Icon);
            var projectIcon = window.Icon;
            Assert.IsNotNull(Find<Button>(strip, "Office.Editor.Sheet.primary"));
            Assert.IsNotNull(Find<Button>(strip, "Office.Editor.Sheet.activity"));
            Assert.IsNull(Find<Button>(strip, "Office.Editor.Sheet.ai"));
            CaptureWindow(window, "xlsx-inside-project");
            Click(window, "Office.Editor.Read");
            await Until(() => !window.IsEditing && strip.IsVisible, 80);
            Assert.AreSame(projectIcon, window.Icon);
            Click(window, "Office.Editor.Sheet.activity");
            await Until(() => window.IsEditing && Find<ListBox>(window, "Office.Editor.Journal.Entries")?.IsVisible == true);
            Click(window, "Office.Editor.Sheet.primary");
            await Until(() => Find<Button>(window, "Office.Workbook.Tab." + second)?.IsVisible == true);
            Assert.IsTrue(await window.CloseEditorAsync());
            window = group.Open(project);
            await Until(() => Find<Button>(window, "Office.Workbook.Tab." + second)?.IsVisible == true);
            Assert.IsTrue(Find<DockPanel>(window, "Office.Editor.SheetStrip")!.IsVisible);
            Assert.IsNotNull(window.Icon);
            Assert.AreEqual(projectIcon.ToString(), window.Icon.ToString());
            Click(window, "Office.Workbook.Tab." + second);
            Assert.AreEqual("Изменение второго листа", Find<TextBox>(window, "Office.Workbook.Formula")!.Text);
            Assert.IsTrue(await window.CloseEditorAsync());
        }
        finally
        {
            ComponentLicenseGate.ConfirmAsync = previousGate;
            if (window.IsVisible)
            {
                var discard = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                discard.Tick += (_, _) =>
                {
                    foreach (Window owned in window.OwnedWindows)
                        Find<Button>(owned, "Office.Editor.Close.Discard")?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                };
                discard.Start();
                try { await window.CloseEditorAsync(); }
                finally { discard.Stop(); }
            }
            foreach (var path in new[] { copy, copy + ".bak", project, project + ".bak" }) File.Delete(path);
            if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }
    }

    private static void CaptureWindow(Window window, string name)
    {
        var path = Environment.GetEnvironmentVariable("LOPATA_WORKBOOK_EVIDENCE");
        if (string.IsNullOrEmpty(path)) return;
        Directory.CreateDirectory(path);
        var visual = (FrameworkElement)window.Content;
        visual.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth),
            (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(path, name + ".png")); encoder.Save(output);
    }

    private static void Click(DependencyObject parent, string id)
    {
        var button = Find<Button>(parent, id); Assert.IsNotNull(button, id);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static T? Find<T>(DependencyObject parent, string? id = null) where T : DependencyObject
    {
        if (parent is T found && (id == null || AutomationProperties.GetAutomationId(parent) == id)) return found;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (Find<T>(VisualTreeHelper.GetChild(parent, i), id) is { } child) return child;
        return null;
    }
    private static async Task Until(Func<bool> test, int seconds = 15)
    {
        var time = Stopwatch.StartNew();
        while (!test()) { if (time.Elapsed > TimeSpan.FromSeconds(seconds)) throw new TimeoutException("Workbook view did not reach the requested state."); await Task.Delay(75); }
    }
}
