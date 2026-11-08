using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using AIHub.Controls.OfficeReader;
using AIHub.Controls.OfficeTextEditor;
using AIHub.Services;
using AIHub.Services.OfficeEditor;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class OfficeWorkbookCalculationWindowTests
{
    private const string SampleHash = "9D1C96292488D1A013C61ADEB75B898B4A79F5A33E1F3B976E59C4E5D44F1C0F";

    [TestMethod, TestCategory("Native")]
    public async Task TimberPriceRecalculatesAtCommitAndSurvivesOrdinaryAndProjectSave()
    {
        var source = Environment.GetEnvironmentVariable("LOPATA_TIMBER_WORKBOOK");
        if (string.IsNullOrWhiteSpace(source)) Assert.Inconclusive("Set LOPATA_TIMBER_WORKBOOK to the original Расчет дерева 2.xlsx sample.");
        Assert.AreEqual(SampleHash, Hash(source), "Use the known unmodified timber workbook.");
        await OfficeProjectUiThread.RunAsync(() => ExerciseWindow(source));
        Assert.AreEqual(SampleHash, Hash(source), "Opening the private copy must preserve the original.");
    }

    private static async Task ExerciseWindow(string source)
    {
        var evidence = Environment.GetEnvironmentVariable("LOPATA_TIMBER_EVIDENCE");
        var folder = evidence == null ? Directory.CreateTempSubdirectory("lopata-timber-").FullName
            : Directory.CreateDirectory(Path.Combine(evidence, "run-" + Guid.NewGuid().ToString("N"))).FullName;
        var copy = Path.Combine(folder, "roundtrip.xlsx");
        var project = Path.Combine(folder, "roundtrip.LOPATAdoxAI");
        File.Copy(source, copy);
        var loc = new LocalizationService(); loc.Load("ru");
        var group = new ReaderWindowGroup(null, loc.T, false);
        var previousGate = ComponentLicenseGate.ConfirmAsync;
        ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
        var history = new Dictionary<string, Dictionary<string, string>>();
        var window = group.OpenEditor(copy);
        try
        {
            DataGrid grid = await Ready(window);

            async Task Check(string step, params (string Address, int Value)[] cells)
            {
                var displayed = new Dictionary<string, string>();
                foreach (var (address, value) in cells)
                {
                    string observed = "";
                    await Until(() =>
                    {
                        observed = (Content<TextBlock>(grid, address)).Text;
                        return DisplaysNumber(observed, value);
                    }, () => $"{step}, {address}: expected {value}, displayed '{observed}'.");
                    displayed[address] = observed;
                }
                history.Add(step, displayed);
            }

            await Check("initial", ("G2", 40), ("H2", 104), ("I2", 2080), ("F18", 28794), ("H18", 29994));
            var visibleSubtotal = Content<TextBlock>(grid, "F18");
            EditGrid(grid, "F2", "21");
            // Leave a second editor open while the first committed edit reaches
            // the actual draft on disk. The timer must not commit partial input.
            var editor = Find<OfficeTextEditorView>(window)!;
            var work = (OfficeTextWork)typeof(OfficeTextEditorView)
                .GetField("_work", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
            SetCurrent(grid, "K16"); grid.Focus(); Assert.IsTrue(grid.BeginEdit());
            var unfinished = Content<TextBox>(grid, "K16");
            unfinished.SetCurrentValue(TextBox.TextProperty, "=H18+");
            await Until(() => ReadNumericCache(work.TemporaryProjectPath, "H18") == 30098,
                () => "The draft did not checkpoint the preceding committed edit while a cell was being typed.");
            Assert.AreSame(unfinished, Content<TextBox>(grid, "K16"));
            Assert.AreEqual("=H18+", unfinished.Text);
            Assert.AreEqual(SampleHash, Hash(copy));
            Assert.IsTrue(grid.CancelEdit(DataGridEditingUnit.Cell));
            grid.CancelEdit(DataGridEditingUnit.Row);
            Assert.AreEqual("", Content<TextBlock>(grid, "K16").Text);
            await Until(() => DisplaysNumber(visibleSubtotal.Text, 28898),
                () => $"Existing F18 still displays '{visibleSubtotal.Text}'.");
            await Check("F2=21", ("G2", 42), ("H2", 104), ("I2", 2184), ("F18", 28898), ("H18", 30098));
            if (evidence != null) Capture(window, Path.Combine(folder, "after-F2-21.png"));
            EditFormulaBar(window, grid, "F2", "20");
            await Check("F2=20", ("I2", 2080), ("F18", 28794), ("H18", 29994));
            EditGrid(grid, "F3", "10");
            await Check("F3=10", ("G3", 20), ("I3", 2800), ("D16", 114), ("F18", 29634), ("H18", 30834));
            EditFormulaBar(window, grid, "E3", "75");
            await Check("E3=75", ("H3", 300), ("I3", 3000), ("F18", 29834), ("H18", 31034));
            SetCurrent(grid, "H3");
            StringAssert.StartsWith(Find<TextBox>(window, "Office.Workbook.Formula")!.Text, "=",
                "Selecting a shared-formula follower must expose its formula while the grid shows its result.");
            EditGrid(grid, "J2", "1400");
            EditFormulaBar(window, grid, "K18", "=H18-J2");
            await Check("new formula", ("F18", 29834), ("H18", 31234), ("K18", 29834));
            foreach (var errorCell in new[] { "G10", "G11" })
                StringAssert.StartsWith(Content<TextBlock>(grid, errorCell).Text, "#", errorCell);

            Assert.AreEqual(SampleHash, Hash(copy), "The displayed results must update before explicit save.");
            ClickSave(window);
            await Until(() => grid.IsEnabled && ReadNumericCache(copy, "H18") == 31234, () => "The saved XLSX did not contain the recalculated total.");
            Assert.IsTrue(await window.CloseEditorAsync());
            window = group.OpenEditor(copy);
            grid = await Ready(window);
            await Check("XLSX reopened", ("G3", 20), ("F18", 29834), ("H18", 31234), ("K18", 29834));

            var create = Find<Menu>(window)!.Items.OfType<MenuItem>().SelectMany(menu => menu.Items.OfType<MenuItem>())
                .Single(menu => AutomationProperties.GetAutomationId(menu) == "Office.Editor.File.CreateProject");
            var dialog = OfficeSheetWindowTests.CommonDialog(window, project);
            create.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); await dialog;
            await Until(() => grid.IsEnabled && File.Exists(project) && Find<Button>(window, "Office.Editor.AddSheet")?.IsVisible == true,
                () => "The XLSX was not saved as a project.");
            EditFormulaBar(window, grid, "B3", "5");
            await Check("B3=5 in project", ("H3", 375), ("I3", 3750), ("F18", 30584), ("H18", 31984), ("K18", 30584));
            ClickSave(window);
            await Until(() => grid.IsEnabled && ReadNumericCache(project, "H18") == 31984, () => "The project did not store the refreshed workbook.");
            Assert.IsTrue(await window.CloseEditorAsync());
            window = group.Open(project);
            grid = await Ready(window);
            await Check("project reopened", ("F18", 30584), ("H18", 31984), ("K18", 30584));
            SetCurrent(grid, "K18");
            Assert.AreEqual("=H18-J2", Find<TextBox>(window, "Office.Workbook.Formula")!.Text);
            Assert.IsTrue(await window.CloseEditorAsync());
            if (evidence != null)
                await File.WriteAllTextAsync(Path.Combine(folder, "window.json"), JsonSerializer.Serialize(new
                {
                    inputSha256 = SampleHash, inputOriginalPreserved = Hash(source) == SampleHash,
                    pendingEditSurvivedDraftCheckpoint = true,
                    controls = new[] { "WPF DataGrid cell edit", "formula bar Enter", "Save", "Create project" },
                    displayed = history
                }, new JsonSerializerOptions { WriteIndented = true }));
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
            if (evidence == null)
            {
                foreach (var file in new[] { copy, copy + ".bak", project, project + ".bak" }) File.Delete(file);
                if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
            }
        }
    }

    private static void ClickSave(Window window) =>
        Find<Button>(window, "Office.Editor.Save")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static bool DisplaysNumber(string text, int expected) =>
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out var actual) && actual == expected;

    private static void Capture(Window window, string path)
    {
        var surface = (FrameworkElement)window.Content; surface.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth),
            (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
        png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file = File.Create(path); png.Save(file);
    }

    private static async Task<DataGrid> Ready(Window window)
    {
        await Until(() => Find<DataGrid>(window) is { Items.Count: > 17, Columns.Count: > 10 }, () => "Workbook grid did not open.");
        window.UpdateLayout();
        return Find<DataGrid>(window)!;
    }

    // The sample uses only single-letter columns. Read the text presented by the
    // WPF column, rather than calling the workbook model or calculator directly.
    private static T Content<T>(DataGrid grid, string address) where T : FrameworkElement
    {
        var row = grid.Items[int.Parse(address.AsSpan(1), CultureInfo.InvariantCulture) - 1];
        var column = grid.Columns[address[0] - 'A'];
        if (column.GetCellContent(row) is T visual) return visual;
        grid.ScrollIntoView(row, column); grid.UpdateLayout();
        return column.GetCellContent(row) as T ?? throw new InvalidOperationException($"No {typeof(T).Name} at {address}.");
    }

    private static void SetCurrent(DataGrid grid, string address)
    {
        var row = grid.Items[int.Parse(address.AsSpan(1), CultureInfo.InvariantCulture) - 1];
        var column = grid.Columns[address[0] - 'A'];
        grid.ScrollIntoView(row, column); grid.UpdateLayout(); grid.CurrentCell = new(row, column);
    }

    private static void EditGrid(DataGrid grid, string address, string input)
    {
        SetCurrent(grid, address); grid.Focus();
        Assert.IsTrue(grid.BeginEdit(), address);
        Content<TextBox>(grid, address).SetCurrentValue(TextBox.TextProperty, input);
        Assert.IsTrue(grid.CommitEdit(DataGridEditingUnit.Cell, true), address);
        Assert.IsTrue(grid.CommitEdit(DataGridEditingUnit.Row, true), address);
    }

    private static void EditFormulaBar(Window window, DataGrid grid, string address, string input)
    {
        SetCurrent(grid, address);
        var box = Find<TextBox>(window, "Office.Workbook.Formula")!;
        box.Focus(); box.Text = input;
        box.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(box)!, 0, Key.Enter)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
    }

    private static decimal? ReadNumericCache(string path, string address)
    {
        try
        {
            using var file = ZipFile.OpenRead(path);
            using var embedded = file.GetEntry("sheets/primary/workbook.xlsx")?.Open();
            using var workbook = embedded == null ? null : new ZipArchive(embedded, ZipArchiveMode.Read);
            using var sheet = (workbook ?? file).GetEntry("xl/worksheets/sheet1.xml")?.Open();
            if (sheet == null) return null;
            XNamespace x = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var cell = XDocument.Load(sheet).Descendants(x + "c").Single(c => (string?)c.Attribute("r") == address);
            return decimal.TryParse((string?)cell.Element(x + "v"), NumberStyles.Float, CultureInfo.InvariantCulture,
                out var number) ? number : null;
        }
        catch (IOException) { return null; }
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static T? Find<T>(DependencyObject parent, string? id = null) where T : DependencyObject
    {
        if (parent is T match && (id == null || AutomationProperties.GetAutomationId(parent) == id)) return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (Find<T>(VisualTreeHelper.GetChild(parent, i), id) is { } child) return child;
        return null;
    }

    private static async Task Until(Func<bool> condition, Func<string> failure)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException(failure());
            await Task.Delay(50);
        }
    }
}
