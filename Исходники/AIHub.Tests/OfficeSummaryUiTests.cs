using System.IO;
using System.Reflection;
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
public sealed class OfficeSummaryUiTests
{
    [TestMethod]
    public Task CancelAndCloseWhileReadingReleaseTheView()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LOPATA_SUMMARY_UI"))) Assert.Inconclusive("Opt in to WPF windows.");
        return OfficeProjectUiThread.RunAsync(async () =>
        {
            var loc = new LocalizationService(); loc.Load("ru");
            var owner = new ReaderWindow(null, loc.T, false); owner.Show();
            try
            {
                foreach (var cancelFirst in new[] { true, false })
                {
                    var journal = new OfficeAiJournal("primary", "summary", (_, _) => Task.CompletedTask);
                    var popup = new OfficeSummaryWindow(owner, loc.T, false, async token =>
                    { await Task.Delay(Timeout.Infinite, token); return ""; }, journal);
                    popup.Show();
                    await Wait(() => Find<System.Windows.Shapes.Path>(popup).Any(p => p.IsVisible));
                    if (cancelFirst)
                    {
                        Find<Button>(popup).Single(b => AutomationProperties.GetAutomationId(b) == "Office.Summary.Cancel")
                            .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                        await Wait(() => Find<TextBlock>(popup).Any(t => t.Text == loc.T("OfficeSummary.Cancelled")));
                        Assert.IsFalse(Find<Button>(popup).Single(b => AutomationProperties.GetAutomationId(b) == "Office.Summary.Copy").IsEnabled);
                    }
                    popup.Close();
                    await Wait(() => !popup.IsVisible);
                }
            }
            finally { owner.Close(); }
        });
    }

    [TestMethod, DataRow(true, true), DataRow(true, false), DataRow(false, true), DataRow(false, false)]
    public Task ToolbarSummarizesSelectionOrWholeDocumentInCorrectSurface(bool editor, bool selection)
    {
        var proof = Environment.GetEnvironmentVariable("LOPATA_SUMMARY_UI");
        if (string.IsNullOrEmpty(proof)) Assert.Inconclusive("Opt in to the installed model and WPF windows.");
        return OfficeProjectUiThread.RunAsync(async () =>
        {
            Directory.CreateDirectory(proof);
            var folder = Directory.CreateTempSubdirectory("lopata-summary-ui-");
            var source = Path.Combine(folder.FullName, "source.docx");
            var original = OfficeTextFormats.PlainDocument("Maria leads the workshop. The meeting is on Friday at 10. The budget is 250 dollars.\nThe appendix describes a journey to Mars.");
            File.WriteAllBytes(source, original);
            var loc = new LocalizationService(); loc.Load("ru");
            var group = new ReaderWindowGroup(null, loc.T, true);
            var window = editor ? group.OpenEditor(source) : group.Open(source);
            var name = (editor ? "editor" : "reader") + (selection ? "-selection" : "-whole");
            var buttonId = editor ? "Office.Editor.Summary" : "Office.Reader.Summary";
            Button? Button(string id, DependencyObject? root = null) => Find<Button>(root ?? window)
                .FirstOrDefault(b => AutomationProperties.GetAutomationId(b) == id && b.IsVisible && b.IsEnabled);
            Window? popup = null;
            try
            {
                await Wait(() => Button(buttonId) != null);
                if (selection)
                {
                    if (editor)
                    {
                        var browser = Find<OfficeTextBrowser>(window).Single(b => b.IsVisible);
                        await browser.RequestAsync(new { action = "command", name = "SelectAll" }, CancellationToken.None);
                    }
                    else
                    {
                        var field = typeof(ReaderWindow).GetField("_selection", BindingFlags.Instance | BindingFlags.NonPublic)!;
                        var range = (ReaderTextSelection)field.GetValue(window)!;
                        range.Begin(0, 0, false); range.Extend(0, 80);
                    }
                }
                _ = window.Dispatcher.BeginInvoke(() => Button(buttonId)!.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)));
                DependencyObject Surface() => editor ? window : popup!;
                if (!editor) await Wait(() => (popup = window.OwnedWindows.Cast<Window>().FirstOrDefault(w => w is OfficeSummaryWindow)) != null);
                await Wait(() => Find<OfficeSummaryView>(Surface()).Any());
                if (editor)
                {
                    Assert.IsNull(window.OwnedWindows.Cast<Window>().FirstOrDefault(w => w is OfficeSummaryWindow));
                    var tab = Find<TabControl>(window).Single(t => t.SelectedItem is TabItem selected && AutomationProperties.GetAutomationId(selected) == "Office.Editor.AiTab");
                    Assert.IsNotNull(tab);
                }
                var spinner = Find<System.Windows.Shapes.Path>(Surface()).Single(p => AutomationProperties.GetAutomationId(p) == "Office.Summary.Spinner");
                Assert.IsTrue(spinner.IsVisible);
                var rotation = (RotateTransform)spinner.RenderTransform;
                var first = rotation.Angle; await Task.Delay(160); Assert.AreNotEqual(first, rotation.Angle);
                Capture((FrameworkElement)Surface(), Path.Combine(proof, name + "-working.png"));
                await Wait(() => Button("Office.Summary.Copy", Surface()) != null, seconds: 120);
                var text = Find<TextBox>(Surface()).Single(t => AutomationProperties.GetAutomationId(t) == "Office.Summary.Result").Text;
                Assert.IsTrue(text.Length > 10 && text.Any(c => c is >= 'А' and <= 'я'));
                Assert.IsFalse(spinner.IsVisible);
                File.WriteAllText(Path.Combine(proof, name + ".txt"), text);
                Capture((FrameworkElement)Surface(), Path.Combine(proof, name + "-ready.png"));
                if (!editor)
                {
                    var output = Path.Combine(proof, name + "-saved.txt");
                    var saveDialog = OfficeSheetWindowTests.CommonDialog(popup!, output);
                    _ = popup!.Dispatcher.BeginInvoke(() => Button("Office.Summary.Save", popup)!.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)));
                    await saveDialog; await Wait(() => File.Exists(output));
                    StringAssert.Contains(File.ReadAllText(output), "document summary");
                    popup.Close(); popup = null;
                }
                CollectionAssert.AreEqual(original, File.ReadAllBytes(source));
            }
            finally
            {
                popup?.Close();
                var discard = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
                discard.Tick += (_, _) =>
                {
                    foreach (Window dialog in window.OwnedWindows)
                        Button("Office.Editor.Close.Discard", dialog)?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                };
                discard.Start();
                try { if (editor) await window.CloseEditorAsync(); else window.Close(); }
                finally { discard.Stop(); folder.Delete(true); }
            }
        });
    }

    private static async Task Wait(Func<bool> predicate, int seconds = 45)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (!predicate()) { if (DateTime.UtcNow > end) Assert.Fail("Summary UI did not reach the expected state."); await Task.Delay(60); }
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
    private static void Capture(FrameworkElement visual, string path)
    {
        var image = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
