using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using AIHub.Controls;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using ListView = System.Windows.Controls.ListView;
using TextBox = System.Windows.Controls.TextBox;
using DataObject = System.Windows.DataObject;

namespace AIHub.Tests;

public sealed partial class BookConversionUiTests
{
    [TestMethod, DataRow(false), DataRow(true)]
    public Task CheckedSourcesAloneAreConvertedAndMergedInTheirReorderedSequence(bool merge) => Sta(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "LOPATA-source-actions-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var paths = Enumerable.Range(1, 3).Select(i => Path.Combine(root, i + ".txt")).ToArray();
        for (var i = 0; i < paths.Length; i++) File.WriteAllText(paths[i], new[] { "First included paragraph.", "Excluded private paragraph.", "Third included paragraph." }[i]);
        var hashes = paths.Select(path => SHA256.HashData(File.ReadAllBytes(path))).ToArray();
        var owner = new Window(); Theme(owner, true); owner.Show();
        var localizer = new LocalizationService(); localizer.Load("ru");
        var window = new BookConversionWindow(owner, localizer.T);
        try
        {
            window.Show(); Click(Find<Button>(window, "Simple")); await Invoke(window, "AddPathsAsync", (object)paths); await Render(window);
            var files = Find<ListView>(window, "Files");
            ToggleSource(files, files.Items[1]); files.SelectedItem = files.Items[1]; // Keyboard focus/highlight must not select conversion inputs.
            await Invoke(window, "ReorderSource", files.Items[2], 0);
            var ordered = files.Items.Cast<object>().ToArray();
            files.SelectedItem = ordered[2];
            Assert.IsFalse((bool)ordered[2].GetType().GetProperty("IsChecked")!.GetValue(ordered[2])!);
            Find<CheckBox>(window, "Merge").IsChecked = merge;
            Find<ComboBox>(window, "Format").SelectedItem = "FB2";
            var output = Path.Combine(root, "out"); Find<TextBox>(window, "OutputDirectory").Text = output;
            await ConvertAndApproveReview(window, output);
            var results = Directory.GetFiles(output, "*.fb2"); Assert.AreEqual(merge ? 1 : 2, results.Length);
            var text = string.Join("\n", results.Select(File.ReadAllText));
            StringAssert.Contains(text, "First included paragraph."); StringAssert.Contains(text, "Third included paragraph.");
            Assert.IsFalse(text.Contains("Excluded private paragraph.", StringComparison.Ordinal));
            if (merge) Assert.IsTrue(text.IndexOf("Third included", StringComparison.Ordinal) < text.IndexOf("First included", StringComparison.Ordinal));
            Assert.AreEqual("", ordered[2].GetType().GetProperty("Output")!.GetValue(ordered[2]));
            Assert.AreEqual(localizer.T("BookConverter.Queued"), ordered[2].GetType().GetProperty("Status")!.GetValue(ordered[2]));
            for (var i = 0; i < paths.Length; i++) CollectionAssert.AreEqual(hashes[i], SHA256.HashData(File.ReadAllBytes(paths[i])));
        }
        finally { window.Close(); owner.Close(); Directory.Delete(root, true); }
    });

    [TestMethod, DataRow("ru", true), DataRow("en", false)]
    public Task SourceMenuChecksRemovalAndNarrowLayoutWorkWithoutHorizontalScrolling(string language, bool dark) => Sta(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "LOPATA-source-layout-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        for (var i = 0; i < 25; i++) File.WriteAllText(Path.Combine(root, i.ToString("00") + "-" + new string('Я', 90) + ".txt"), "A retained source.");
        var owner = new Window(); Theme(owner, dark); owner.Show();
        var localizer = new LocalizationService(); localizer.Load(language);
        var window = new BookConversionWindow(owner, localizer.T) { Width = 850, Height = 700 };
        try
        {
            window.Show(); Click(Find<Button>(window, "Simple")); await Invoke(window, "AddPathsAsync", (object)new[] { root }); await Render(window);
            var files = Find<ListView>(window, "Files");
            Assert.AreEqual(25, files.Items.Count);
            Assert.IsFalse(Desc<Button>(window).Any(button => new[] { "BookConverter.Up", "BookConverter.Down", "BookConverter.AddFiles", "BookConverter.AddFolder" }.Contains(AutomationProperties.GetAutomationId(button))));
            var add = Find<Button>(window, "AddSources"); Click(add);
            Assert.IsTrue(add.ContextMenu!.IsOpen); Assert.AreEqual(2, add.ContextMenu.Items.Count); add.ContextMenu.IsOpen = false;
            var scroll = VisualDesc<ScrollViewer>(files).First();
            Assert.AreEqual(ScrollBarVisibility.Disabled, scroll.HorizontalScrollBarVisibility);
            Assert.AreEqual(Visibility.Collapsed, scroll.ComputedHorizontalScrollBarVisibility);
            Assert.IsTrue(scroll.ExtentWidth <= scroll.ViewportWidth + 2, $"Hidden content overflow: {scroll.ExtentWidth}/{scroll.ViewportWidth}");
            SaveEvidence(window, "sources-" + language + "-" + dark);
            Click(Find<CheckBox>(window, "CheckAll"));
            Assert.IsFalse(Find<Button>(window, "Start").IsEnabled); Assert.IsFalse(Find<Button>(window, "Remove").IsEnabled);
            ToggleSource(files, files.Items[0]); ToggleSource(files, files.Items[1]);
            Assert.IsNull(Find<CheckBox>(window, "CheckAll").IsChecked);
            var removePaths = files.Items.Cast<object>().Take(2).Select(value => (string)value.GetType().GetProperty("Path")!.GetValue(value)!).ToArray();
            Click(Find<Button>(window, "Remove")); Assert.AreEqual(23, files.Items.Count);
            Assert.IsTrue(removePaths.All(File.Exists), "Removing sources from the list must not delete files.");
            Click(Find<CheckBox>(window, "CheckAll")); Assert.IsTrue(files.Items.Cast<object>().All(value => (bool)value.GetType().GetProperty("IsChecked")!.GetValue(value)!));
            var item = files.Items[0];
            var payloadType = typeof(BookConversionWindow).GetNestedType("SourceDrag", BindingFlags.NonPublic)!;
            var own = Activator.CreateInstance(payloadType, window, item)!;
            Assert.AreSame(item, typeof(BookConversionWindow).GetMethod("DraggedSource", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [new DataObject("AIHub.BookConverter.SourceRow", own)]));
            var last = files.Items[files.Items.Count - 1]; files.ScrollIntoView(last); files.UpdateLayout();
            var lastRow = (FrameworkElement)files.ItemContainerGenerator.ContainerFromItem(last);
            var point = lastRow.TranslatePoint(new System.Windows.Point(lastRow.ActualWidth / 2, lastRow.ActualHeight * .75), files);
            var ownData = new DataObject("AIHub.BookConverter.SourceRow", own);
            var over = SourceDragEvent(ownData, files, point, System.Windows.DragDrop.DragOverEvent);
            files.RaiseEvent(over); Assert.AreEqual(System.Windows.DragDropEffects.Move, over.Effects);
            files.RaiseEvent(SourceDragEvent(ownData, files, point, System.Windows.DragDrop.DropEvent));
            Assert.AreSame(item, files.Items[files.Items.Count - 1], "The actual routed Drop must apply the new order.");
            var other = new BookConversionWindow(owner, localizer.T);
            try
            {
                var foreign = Activator.CreateInstance(payloadType, other, item)!;
                Assert.IsNull(typeof(BookConversionWindow).GetMethod("DraggedSource", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [new DataObject("AIHub.BookConverter.SourceRow", foreign)]));
                var foreignOver = SourceDragEvent(new DataObject("AIHub.BookConverter.SourceRow", foreign), files, point, System.Windows.DragDrop.DragOverEvent);
                files.RaiseEvent(foreignOver); Assert.AreEqual(System.Windows.DragDropEffects.None, foreignOver.Effects);
            }
            finally { other.Close(); }
            var extra = Path.Combine(root, "Imported-from-Explorer.txt"); File.WriteAllText(extra, "An additional source.");
            var beforeImport = files.Items.Count;
            files.RaiseEvent(SourceDragEvent(new DataObject(System.Windows.DataFormats.FileDrop, new[] { extra }), files,
                new System.Windows.Point(50, 70), System.Windows.DragDrop.DropEvent));
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!window.AreSourcesEditable && DateTime.UtcNow < deadline) await Task.Delay(15);
            Assert.AreEqual(beforeImport + 1, files.Items.Count, "Explorer drops must still add sources instead of being swallowed by row reordering.");
        }
        finally { window.Close(); owner.Close(); Directory.Delete(root, true); }
    });

    [TestMethod]
    public Task UncheckedTextDoesNotBlockGraphicOnlyOutputFormats() => Sta(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "LOPATA-source-capabilities-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "1.txt"), "Text source.");
        using (var bitmap = new SkiaSharp.SKBitmap(8, 8))
        {
            bitmap.Erase(SkiaSharp.SKColors.Green); using var png = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(root, "2.png"), png.ToArray());
        }
        var owner = new Window(); Theme(owner, true); owner.Show();
        var localizer = new LocalizationService(); localizer.Load("ru");
        var window = new BookConversionWindow(owner, localizer.T);
        try
        {
            window.Show(); Click(Find<Button>(window, "Simple")); await Invoke(window, "AddPathsAsync", (object)new[] { root }); await Render(window);
            var formats = Find<ComboBox>(window, "Format"); Assert.IsFalse(formats.Items.Cast<string>().Contains("CBZ"));
            var files = Find<ListView>(window, "Files"); ToggleSource(files, files.Items[0]);
            Assert.IsTrue(formats.Items.Cast<string>().Contains("CBZ"), "Only checked sources should restrict output capabilities.");
        }
        finally { window.Close(); owner.Close(); Directory.Delete(root, true); }
    });

    private static void ToggleSource(ListView list, object entry)
    {
        list.ScrollIntoView(entry); list.UpdateLayout();
        var checkbox = VisualDesc<CheckBox>(list).Single(value => ReferenceEquals(value.DataContext, entry));
        var provider = (IToggleProvider)new CheckBoxAutomationPeer(checkbox).GetPattern(PatternInterface.Toggle)!;
        provider.Toggle();
    }
    private static void Click(CheckBox checkbox) => checkbox.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
    private static System.Windows.DragEventArgs SourceDragEvent(System.Windows.IDataObject data, ListView target, System.Windows.Point point, RoutedEvent routedEvent)
    {
        var args = (System.Windows.DragEventArgs)Activator.CreateInstance(typeof(System.Windows.DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
            null, [data, System.Windows.DragDropKeyStates.LeftMouseButton, System.Windows.DragDropEffects.Move, target, point], null)!;
        args.RoutedEvent = routedEvent; return args;
    }
    private static IEnumerable<T> VisualDesc<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T found) yield return found;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in VisualDesc<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
