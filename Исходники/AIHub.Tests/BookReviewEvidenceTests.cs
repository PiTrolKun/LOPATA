using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Services.Books;
using SkiaSharp;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class BookReviewEvidenceTests
{
    [TestMethod]
    public void AlignmentUsesExactWordsNormalizedWhitespaceAndExplicitOccurrence()
    {
        const string actual = "Повтор\nтекста. Другой фрагмент. Повтор\u00a0 текста.";
        var evidence = new BookReviewEvidence(["source.pdf"],
            [new("Повтор текста.", BookReviewMarkKind.AiProcessed, "source.pdf", Occurrence: 1),
             new("Другой текст.", BookReviewMarkKind.Questionable, "source.pdf"),
             new("фрагмент", BookReviewMarkKind.Questionable, "foreign.pdf")]);
        var resolved = BookReviewEvidenceResolver.Resolve(actual, evidence, true);
        Assert.AreEqual(1, resolved.Marks.Length); Assert.AreEqual(2, resolved.Unresolved);
        Assert.AreEqual(actual.LastIndexOf("Повтор", StringComparison.Ordinal), resolved.Marks[0].Start);
        Assert.AreEqual("Повтор\u00a0 текста.", actual.Substring(resolved.Marks[0].Start, resolved.Marks[0].Length));
    }

    [TestMethod]
    public void EvidenceInvalidatesAfterAnEditEvenWhenSameWordsStillExistElsewhere()
    {
        var document = Document("<p>Повтор текста.</p><p>Повтор текста.</p>");
        var session = Session(document);
        session.Evidence = new(["source.pdf"], [new("Повтор текста.", BookReviewMarkKind.AiProcessed, "source.pdf", Occurrence: 1)]);
        Assert.IsTrue(session.IsEvidenceCurrent);
        var edited = session.Document; edited.UpdateBlockText(edited.Blocks.First().Id, "Другой текст."); session.SetDocument(edited);
        Assert.IsFalse(session.IsEvidenceCurrent);
        var resolution = BookReviewEvidenceResolver.Resolve("Другой текст. Повтор текста.", session.Evidence, session.IsEvidenceCurrent);
        Assert.AreEqual(0, resolution.Marks.Length); Assert.AreEqual(1, resolution.Unresolved);
        // Unchanged editor confirmation does not invalidate the exact revision.
        var unchanged = Session(document); unchanged.Evidence = session.Evidence; unchanged.SetDocument(unchanged.Document);
        Assert.IsTrue(unchanged.IsEvidenceCurrent);
    }

    [TestMethod]
    public Task RedOverridesGreenAcrossInlineRunsAndAuthorColorOutsideMarksSurvives() => Sta(() =>
    {
        var document = new FlowDocument(); var paragraph = new Paragraph(); document.Blocks.Add(paragraph);
        paragraph.Inlines.Add(new Run("Автор: ") { Foreground = Brushes.Blue });
        paragraph.Inlines.Add(new Bold(new Run("точный "))); paragraph.Inlines.Add(new Run("спорный текст"));
        var evidence = new BookReviewEvidence(["source.pdf"],
            [new("точный спорный текст", BookReviewMarkKind.AiProcessed, "source.pdf"),
             new("спорный", BookReviewMarkKind.Questionable, "source.pdf")]);
        Assert.AreEqual(0, BookReviewHighlightLayer.Apply(document, evidence, true, key => key));
        var runs = Runs(paragraph.Inlines).ToArray();
        Assert.AreEqual(Colors.Blue, ((SolidColorBrush)runs.First(run => run.Text == "Автор: ").Foreground).Color);
        Assert.AreEqual((Color)ColorConverter.ConvertFromString("#991B1B"), ((SolidColorBrush)runs.Single(run => run.Text == "спорный").Foreground).Color);
        Assert.AreEqual((Color)ColorConverter.ConvertFromString("#065F46"), ((SolidColorBrush)runs.Single(run => run.Text == "точный ").Foreground).Color);
        Assert.AreEqual("Автор: точный спорный текст\r\n", new TextRange(document.ContentStart, document.ContentEnd).Text);
    });

    [TestMethod]
    public Task HighlightingNeverWritesToActualDocumentOrPublishedFile() => Sta(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "LOPATA-evidence-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "author.html");
            File.WriteAllText(source, "<html><body><p style='color:#3344aa'>Авторский цвет. Спорный текст.</p></body></html>");
            byte[]? preview = null;
            var service = new BookConversionService((session, _) =>
            {
                preview = File.ReadAllBytes(session.PreviewPath); var before = session.Document.CanonicalHtml();
                session.Evidence = new([source], [new("Спорный текст.", BookReviewMarkKind.Questionable, source)]);
                BookReviewFlowRenderer.Create(session.Document, key => key, session.Evidence, true, out var unresolved);
                Assert.AreEqual(0, unresolved); Assert.AreEqual(before, session.Document.CanonicalHtml());
                CollectionAssert.AreEqual(preview, File.ReadAllBytes(session.PreviewPath));
                return Task.FromResult(BookReviewDecision.Save);
            });
            var result = await service.ConvertAsync(new() { Sources = [source], OutputFormat = "html", OutputDirectory = Path.Combine(root, "out") });
            Assert.AreEqual(0, result.Failures.Length, string.Join(";", result.Failures.Select(error => error.Message)));
            Assert.AreEqual(1, result.Outputs.Length); Assert.IsNotNull(preview);
            CollectionAssert.AreEqual(preview, File.ReadAllBytes(result.Outputs[0]));
            var output = File.ReadAllText(result.Outputs[0]); StringAssert.Contains(output, "#3344aa");
            Assert.IsFalse(output.Contains("#991B1B", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(output.Contains("#FEE2E2", StringComparison.OrdinalIgnoreCase));
        }
        finally { Directory.Delete(root, true); }
    });

    [TestMethod]
    public Task SeveralOriginalsRequireAnExplicitMenuAndOpenTheChosenReadOnlyViewer() => Sta(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "LOPATA-originals-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var first = Path.Combine(root, "one.txt"); var second = Path.Combine(root, "two.txt");
        File.WriteAllText(first, "First source"); File.WriteAllText(second, "Second source");
        var before = File.ReadAllBytes(second); var owner = new Window(); owner.Show();
        var session = Session(Document("<p>Converted result.</p>")); session.Evidence = new([first, second, first], []);
        var window = new BookConversionReviewWindow(owner, session, key => key);
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.Background);
            var button = Desc<Button>(window).Single(value => AutomationProperties.GetAutomationId(value) == "BookConverter.Review.OpenOriginal");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.IsNotNull(button.ContextMenu); Assert.AreEqual(2, button.ContextMenu.Items.Count);
            Assert.AreEqual(0, window.OwnedWindows.Count);
            var choice = (MenuItem)button.ContextMenu.Items[1]; Assert.AreEqual(second, choice.Tag);
            choice.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.Background);
            var viewer = window.OwnedWindows.Cast<Window>().OfType<AIHub.FileViewerWindow>().Single();
            var path = typeof(AIHub.FileViewerWindow).GetField("_path", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewer);
            Assert.AreEqual(second, path); Assert.IsTrue(window.IsVisible);
            var textbox = (System.Windows.Controls.TextBox)viewer.FindName("TextViewer"); Assert.IsTrue(textbox.IsReadOnly);
            viewer.Close(); CollectionAssert.AreEqual(before, File.ReadAllBytes(second));
        }
        finally { window.Close(); owner.Close(); Directory.Delete(root, true); }
    });

    [TestMethod]
    public Task ScannedPdfOriginalShowsRealPagesAndNavigationWithoutChangingSource() => Sta(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "LOPATA-original-pdf-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "scan.pdf");
        using (var stream = File.Create(path))
        using (var pdf = SKDocument.CreatePdf(stream))
        {
            foreach (var color in new[] { SKColors.Red, SKColors.Blue })
            { var canvas = pdf.BeginPage(200, 300); canvas.Clear(color); pdf.EndPage(); }
            pdf.Close();
        }
        var before = File.ReadAllBytes(path);
        var viewer = new AIHub.FileViewerWindow(path, true, key => key, originalPdfPages: true);
        try
        {
            viewer.Show(); var image = (System.Windows.Controls.Image)viewer.FindName("ImageViewer");
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (image.Source is null && DateTime.UtcNow < deadline) await Task.Delay(100);
            Assert.IsNotNull(image.Source, ((TextBlock)viewer.FindName("StatusText")).Text);
            Assert.IsTrue(image.Source.Width > 100); var first = image.Source;
            var next = Desc<Button>(viewer).Single(button => AutomationProperties.GetAutomationId(button) == "BookConverter.Original.NextPage");
            Assert.IsTrue(next.IsEnabled); next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            while (ReferenceEquals(first, image.Source) && DateTime.UtcNow < deadline) await Task.Delay(100);
            Assert.AreNotSame(first, image.Source, ((TextBlock)viewer.FindName("StatusText")).Text);
            Assert.IsFalse(next.IsEnabled);
            Assert.IsTrue(Desc<Button>(viewer).Single(button => AutomationProperties.GetAutomationId(button) == "BookConverter.Original.PreviousPage").IsEnabled);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
        }
        finally { viewer.Close(); Directory.Delete(root, true); }
    });

    private static BookReviewDocument Document(string html)
    { var content = new BookContent { Title = "Evidence book" }; content.Parts.Add(new("Chapter", html)); return BookReviewDocument.FromContent(content); }
    private static BookReviewSession Session(BookReviewDocument document) => new("target.html", "html", "source.pdf", document, (book, _) => Task.FromResult(book));
    private static IEnumerable<Run> Runs(InlineCollection inlines)
    { foreach (var inline in inlines) { if (inline is Run run) yield return run; if (inline is Span span) foreach (var nested in Runs(span.Inlines)) yield return nested; } }
    private static IEnumerable<T> Desc<T>(DependencyObject root) where T : DependencyObject
    { for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++) { var child = VisualTreeHelper.GetChild(root, index); if (child is T value) yield return value; foreach (var nested in Desc<T>(child)) yield return nested; } }
    private static Task Sta(Action action) => Sta(() => { action(); return Task.CompletedTask; });
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
}
