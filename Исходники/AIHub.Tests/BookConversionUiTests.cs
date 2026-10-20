using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using ListView = System.Windows.Controls.ListView;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed partial class BookConversionUiTests
{
    [TestMethod, DataRow("ru", true), DataRow("en", false)]
    public Task ModesCapabilitiesOrderAndRealMergePreserveSources(string language, bool dark) => Sta(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "LOPATA-book-ui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var owner = new Window(); Theme(owner, dark); owner.Show();
        var localizer = new LocalizationService(); localizer.Load(language);
        var window = new BookConversionWindow(owner, localizer.T) { Width = 1100, Height = 820 };
        try
        {
            var first = Path.Combine(root, "01-текст.txt"); var second = Path.Combine(root, "02-текст.txt");
            File.WriteAllText(first, "Первый документ — начало истории."); File.WriteAllText(second, "Второй документ — продолжение истории.");
            var before = new[] { SHA256.HashData(File.ReadAllBytes(first)), SHA256.HashData(File.ReadAllBytes(second)) };
            window.Show(); await Render(window);
            Assert.IsTrue(Find<Button>(window, "Smart").IsVisible);
            Assert.IsTrue(Desc<TextBlock>(window).Any(value => value.IsVisible && value.Text == localizer.T("BookConverter.SmartHint")));
            Assert.IsTrue(Desc<TextBlock>(window).Any(value => value.IsVisible && value.Text == localizer.T("BookConverter.SimpleHint")));
            Assert.IsFalse(Find<Button>(window, "Start").IsVisible);
            // Smart preparation has its own dependency-free STA tests; this regression check exercises the simple mode.
            Click(Find<Button>(window, "Simple"));
            await Invoke(window, "AddPathsAsync", (object)new[] { first, second, first }); await Render(window);
            var list = Find<ListView>(window, "Files"); Assert.AreEqual(2, list.Items.Count);
            var format = Find<ComboBox>(window, "Format"); Assert.IsFalse(format.Items.Cast<string>().Contains("CBZ"));
            list.SelectedItem = list.Items[1]; var moved = list.SelectedItem;
            await Invoke(window, "ReorderSource", moved, 0); Assert.AreSame(moved, list.Items[0]);
            Find<CheckBox>(window, "Merge").IsChecked = true;
            Find<TextBox>(window, "TitleField").Text = "Общая книга";
            Find<TextBox>(window, "AuthorField").Text = "Автор теста";
            Find<TextBox>(window, "OutputDirectory").Text = root;
            format.SelectedItem = "FB2";
            Assert.IsFalse(Desc<CheckBox>(window).Any(value => AutomationProperties.GetAutomationId(value) == "BookConverter.ExplorerMenu"));
            await ConvertAndApproveReview(window, root); await Render(window);
            var output = Directory.GetFiles(root, "*.fb2").Single();
            var text = File.ReadAllText(output);
            Assert.IsTrue(text.Contains("Общая книга", StringComparison.Ordinal));
            Assert.IsTrue(text.IndexOf("Второй документ", StringComparison.Ordinal) < text.IndexOf("Первый документ", StringComparison.Ordinal));
            Assert.IsTrue(text.Contains("LOPATA", StringComparison.OrdinalIgnoreCase));
            CollectionAssert.AreEqual(before[0], SHA256.HashData(File.ReadAllBytes(first)));
            CollectionAssert.AreEqual(before[1], SHA256.HashData(File.ReadAllBytes(second)));
            Assert.IsTrue(Find<Button>(window, "OpenResult").IsEnabled);
            Assert.IsTrue(Find<Button>(window, "ShowFolder").IsEnabled);
            if (Environment.GetEnvironmentVariable("LOPATA_BOOK_UI_EVIDENCE") is { Length: > 0 } evidence)
            {
                Directory.CreateDirectory(evidence);
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(evidence, language + "-" + dark + ".png")); encoder.Save(stream);
            }
        }
        finally { window.Close(); owner.Close(); Directory.Delete(root, true); }
    });

    [TestMethod, DataRow("Save"), DataRow("Review"), DataRow("Cancel")]
    public Task ConvertedMarkdownOffersAChoiceAndOnlyTheHtmlResultIsEdited(string action) => Sta(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "LOPATA-book-choice-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source.md"); var outputDirectory = Path.Combine(root, "out");
        File.WriteAllText(source, "# Original title\n\nA **formatted** paragraph.");
        var sourceBytes = File.ReadAllBytes(source);
        var owner = new Window(); Theme(owner, true); owner.Show();
        var localizer = new LocalizationService(); localizer.Load("ru");
        var window = new BookConversionWindow(owner, localizer.T);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        var choiceSeen = false; var reviewSeen = false; var editorSeen = false; var edited = false;
        Exception? failure = null; var deadline = DateTime.UtcNow.AddSeconds(30);
        try
        {
            window.Show(); Click(Find<Button>(window, "Simple"));
            await Invoke(window, "AddPathsAsync", (object)new[] { source });
            Find<ComboBox>(window, "Format").SelectedItem = "HTML";
            Find<TextBox>(window, "OutputDirectory").Text = outputDirectory;
            timer.Tick += (_, _) =>
            {
                try
                {
                    if (DateTime.UtcNow > deadline) throw new TimeoutException("The review/save choice did not complete.");
                    var choice = window.OwnedWindows.OfType<BookConversionChoiceWindow>().SingleOrDefault();
                    if (choice is not null)
                    {
                        choiceSeen = true;
                        Assert.IsFalse(Directory.Exists(outputDirectory));
                        Assert.IsTrue(Desc<TextBlock>(choice).Any(text => text.Text == "source.html"));
                        if (action == "Review") SaveEvidence(choice, "choice-ru-dark");
                        var button = Desc<Button>(choice).Single(value => AutomationProperties.GetAutomationId(value) == "BookConverter.Choice." + action);
                        Click(button); return;
                    }
                    var review = window.OwnedWindows.OfType<BookConversionReviewWindow>().SingleOrDefault();
                    if (review is null) return;
                    reviewSeen = true;
                    Assert.AreEqual("Review", action, "Save immediately/Cancel must never open a viewer.");
                    Assert.IsTrue(Desc<TextBlock>(review).Any(text => text.Text.Contains("source.html", StringComparison.Ordinal)));
                    if (!edited)
                    {
                        edited = true; timer.Stop();
                        _ = window.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                var editor = review.OwnedWindows.OfType<BookDocumentEditorWindow>().Single(); editorSeen = true;
                                Assert.IsTrue(editor.Title.Contains("HTML", StringComparison.Ordinal));
                                SaveEvidence(editor, "editor-html-ru-dark");
                                Desc<TextBox>(editor).Single(text => AutomationProperties.GetAutomationId(text) == "BookConverter.Editor.Text").Text = "Edited converted title";
                                Click(Desc<Button>(editor).Single(value => AutomationProperties.GetAutomationId(value) == "BookConverter.Editor.Apply"));
                            }
                            catch (Exception error) { failure = error; foreach (Window child in review.OwnedWindows) child.Close(); review.Close(); }
                        }), DispatcherPriority.Background);
                        Click(Desc<Button>(review).Single(value => AutomationProperties.GetAutomationId(value) == "BookConverter.Review.Edit"));
                        timer.Start(); return;
                    }
                    var save = Desc<Button>(review).Single(value => AutomationProperties.GetAutomationId(value) == "BookConverter.Review.Save");
                    if (!save.IsEnabled) return;
                    var document = Desc<FlowDocumentScrollViewer>(review).Single().Document;
                    StringAssert.Contains(new System.Windows.Documents.TextRange(document.ContentStart, document.ContentEnd).Text, "Edited converted title");
                    Assert.IsFalse(Directory.Exists(outputDirectory));
                    Click(save);
                }
                catch (Exception error)
                {
                    failure = error; timer.Stop(); Click(Find<Button>(window, "Cancel"));
                }
            };
            timer.Start(); await Invoke(window, "ConvertAsync"); timer.Stop();
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            Assert.IsTrue(choiceSeen); Assert.AreEqual(action == "Review", reviewSeen); Assert.AreEqual(action == "Review", editorSeen);
            CollectionAssert.AreEqual(sourceBytes, File.ReadAllBytes(source));
            if (action == "Cancel") Assert.IsFalse(Directory.Exists(outputDirectory));
            else
            {
                var output = Directory.GetFiles(outputDirectory, "*.html").Single();
                var html = File.ReadAllText(output); StringAssert.Contains(html, "LOPATA");
                StringAssert.Contains(html, action == "Review" ? "Edited converted title" : "Original title");
            }
        }
        finally { timer.Stop(); window.Close(); owner.Close(); Directory.Delete(root, true); }
    });

    private static void SaveEvidence(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("LOPATA_BOOK_UI_EVIDENCE") is not { Length: > 0 } evidence) return;
        Directory.CreateDirectory(evidence); window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(evidence, name + ".png")); encoder.Save(stream);
    }

    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject =>
        Desc<T>(root).Single(value => AutomationProperties.GetAutomationId(value) == "BookConverter." + id);
    private static IEnumerable<T> Desc<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T found) yield return found;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) foreach (var value in Desc<T>(child)) yield return value;
    }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task ConvertAndApproveReview(BookConversionWindow window, string outputDirectory)
    {
        var approved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var choiceSeen = false;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        timer.Tick += (_, _) =>
        {
            try
            {
                var choice = window.OwnedWindows.OfType<Window>().SingleOrDefault(value => AutomationProperties.GetAutomationId(value) == "BookConverter.Choice.Window");
                if (choice is not null)
                {
                    choiceSeen = true;
                    Click(Desc<Button>(choice).Single(value => AutomationProperties.GetAutomationId(value) == "BookConverter.Choice.Review"));
                    return;
                }
                var review = window.OwnedWindows.OfType<Window>().SingleOrDefault(value => AutomationProperties.GetAutomationId(value) == "BookConverter.Review.Window");
                if (review is null)
                {
                    if (DateTime.UtcNow < deadline) return;
                    throw new TimeoutException("The required pre-save review did not open.");
                }
                var save = Desc<Button>(review).Single(value => AutomationProperties.GetAutomationId(value) == "BookConverter.Review.Save");
                if (!save.IsEnabled) return;
                var unpublished = !Directory.Exists(outputDirectory) || Directory.GetFiles(outputDirectory, "*.fb2").Length == 0;
                Click(save); approved.TrySetResult(unpublished);
            }
            catch (Exception error)
            {
                timer.Stop(); approved.TrySetException(error);
                Click(Find<Button>(window, "Cancel"));
            }
        };
        timer.Start();
        try
        {
            await Invoke(window, "ConvertAsync");
            Assert.IsTrue(choiceSeen, "The user must be offered review or immediate saving after conversion.");
            Assert.IsTrue(await approved.Task, "No final file may exist before explicit approval in the review window.");
        }
        finally { timer.Stop(); }
    }
    private static async Task Invoke(BookConversionWindow window, string method, params object[] args)
    {
        var result = typeof(BookConversionWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
        if (result is Task task) await task;
    }
    private static async Task Render(Window window) { window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.Background); window.UpdateLayout(); }
    private static Task Sta(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); completion.TrySetResult(); } catch (Exception error) { completion.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }); Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task.WaitAsync(TimeSpan.FromMinutes(2));
    }
    private static void Theme(FrameworkElement root, bool dark)
    {
        root.Resources["TextPrimaryBrush"] = dark ? Brushes.White : Brushes.Black;
        root.Resources["TextSecondaryBrush"] = dark ? Brushes.LightGray : Brushes.DarkSlateGray;
        root.Resources["PanelBrush"] = dark ? Brushes.MidnightBlue : Brushes.White;
        root.Resources["WindowBackgroundBrush"] = dark ? Brushes.Black : Brushes.WhiteSmoke;
        root.Resources["InputBrush"] = root.Resources["PanelBrush"];
        root.Resources["SecondaryButtonBackgroundBrush"] = root.Resources["PanelBrush"];
        root.Resources["StepBadgeBrush"] = dark ? Brushes.DarkSlateBlue : Brushes.LightBlue;
        root.Resources["LineBrush"] = Brushes.Gray; root.Resources["AccentBrush"] = Brushes.RoyalBlue; root.Resources["UiBodyFontSize"] = 14d;
    }
}
