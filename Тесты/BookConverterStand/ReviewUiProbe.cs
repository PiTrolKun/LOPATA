using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Services.Books;
using AIHub.Services;
using AngleSharp.Html.Parser;
using SkiaSharp;

internal static class ReviewUiProbe
{
    [STAThread]
    private static int Main(string[] args)
    {
        var results = new List<object>();
        var output = args.Length > 0 ? args[0] : Path.Combine(Directory.GetCurrentDirectory(), "Тесты", "BookConverterStand", "review-ui-result.json");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var owner = new Window { Width = 600, Height = 400, Title = "Book review UI stand" };
        Theme(owner); app.MainWindow = owner; owner.Show();
        var success = false;
        try
        {
            Run(owner, cancel: false, results);
            Run(owner, cancel: true, results);
            Run(owner, cancel: true, results, escape: true);
            RunNoOpMultiline(owner, results);
            success = true;
        }
        catch (Exception ex) { results.Add(new { failure = ex.ToString() }); }
        finally
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            File.WriteAllText(output, JsonSerializer.Serialize(new { passed = success, results }, new JsonSerializerOptions { WriteIndented = true }));
            owner.Close(); app.Shutdown();
        }
        return success ? 0 : 1;
    }

    private static void Run(Window owner, bool cancel, List<object> results, bool escape = false)
    {
        var document = Document(); var changed = "Edited text remains in the converted preview.";
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerFinished = false; var closeWasDeferred = false; Exception? failure = null;
        var privatePath = Path.Combine(Path.GetTempPath(), "lopata-review-ui-" + Guid.NewGuid().ToString("N") + ".html");
        File.WriteAllText(privatePath, document.GetHtml());
        Func<BookReviewDocument, CancellationToken, Task<BookReviewDocument>> refresh = async (updated, token) =>
        {
            using var registration = token.Register(() => cancellationObserved.TrySetResult());
            started.TrySetResult();
            try
            {
                // Deliberately keep the writer alive after cancellation, as a real converter may need cleanup.
                await release.Task;
                token.ThrowIfCancellationRequested();
                await File.WriteAllTextAsync(privatePath, updated.GetHtml(), token);
                return updated.Clone();
            }
            finally { workerFinished = true; }
        };
        var session = (BookReviewSession)Activator.CreateInstance(typeof(BookReviewSession), BindingFlags.Instance | BindingFlags.NonPublic,
            null, [privatePath, "html", "Synthetic book", document, refresh], null)!;
        var localizer = new LocalizationService(); localizer.Load("ru");
        var review = new BookConversionReviewWindow(owner, session, localizer.T);
        var watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        watchdog.Tick += (_, _) => { failure ??= new TimeoutException("UI review test timed out."); release.TrySetResult(); review.Close(); };
        review.Loaded += async (_, _) =>
        {
            try
            {
                review.UpdateLayout();
                Require(Images(Find<FlowDocumentScrollViewer>(review, "BookConverter.Review.Document")).Any(image => image.Source is not null), "The inline SVG cover must render as a real Image before editing.");
                if (!cancel) Capture(review, "review-ru-dark.png");
                // The nested editor's modal dispatcher processes this queued action after the Edit click.
                _ = review.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        var editor = Application.Current.Windows.OfType<BookDocumentEditorWindow>().Single();
                        editor.UpdateLayout();
                        if (!cancel) Capture(editor, "editor-ru-dark.png");
                        var text = Find<TextBox>(editor, "BookConverter.Editor.Text");
                        text.Text = changed;
                        Click(editor, "BookConverter.Editor.Apply");
                    }
                    catch (Exception ex) { failure = ex; release.TrySetResult(); review.Close(); }
                }), DispatcherPriority.Background);
                Click(review, "BookConverter.Review.Edit");
                await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Require(!Find<Button>(review, "BookConverter.Review.Save").IsEnabled, "Save must be disabled while the artifact writer is active.");
                if (cancel)
                {
                    if (escape)
                        review.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(review)!, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                    else Click(review, "BookConverter.Review.Cancel");
                    await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    closeWasDeferred = review.IsVisible && !workerFinished;
                    Require(closeWasDeferred, "Dialog must stay open until the cancelled writer really finishes.");
                    release.TrySetResult();
                }
                else
                {
                    release.TrySetResult();
                    var until = DateTime.UtcNow.AddSeconds(10);
                    while (!Find<Button>(review, "BookConverter.Review.Save").IsEnabled)
                    { if (DateTime.UtcNow > until) throw new TimeoutException("Updated preview never became savable."); await Task.Delay(15); }
                    var flow = Find<FlowDocumentScrollViewer>(review, "BookConverter.Review.Document").Document;
                    Require(new TextRange(flow.ContentStart, flow.ContentEnd).Text.Contains(changed), "Viewer must display refreshed edited content.");
                    Require(new HtmlParser().ParseDocument(File.ReadAllText(privatePath)).Body!.TextContent.Contains(changed), "The actual private target must contain the edit before Save.");
                    Click(review, "BookConverter.Review.Save");
                }
            }
            catch (Exception ex) { failure = ex; release.TrySetResult(); review.Close(); }
        };
        watchdog.Start();
        review.ShowDialog();
        watchdog.Stop();
        if (failure is not null) throw failure;
        Require(workerFinished && !session.IsRefreshing, "Review callback returned before its writer completed.");
        Require(review.Decision == (cancel ? BookReviewDecision.Cancel : BookReviewDecision.Save), "Unexpected review decision.");
        if (cancel) Require(closeWasDeferred, "Cancellation was not tested while the writer was active.");
        results.Add(new { scenario = cancel ? escape ? "busy Escape waits for writer" : "busy Cancel waits for writer" : "edit apply refresh save", passed = true, workerFinished, closeWasDeferred });
        File.Delete(privatePath);
    }

    private static void RunNoOpMultiline(Window owner, List<object> results)
    {
        var document = Document("<p>Первая строка\nВторая <strong>строка</strong>\nТретья строка.</p><p>Следующий абзац.</p>");
        var privatePath = Path.Combine(Path.GetTempPath(), "lopata-review-noop-" + Guid.NewGuid().ToString("N") + ".html");
        File.WriteAllText(privatePath, document.GetHtml()); var before = File.ReadAllBytes(privatePath);
        var writerCalls = 0; Exception? failure = null;
        Func<BookReviewDocument, CancellationToken, Task<BookReviewDocument>> refresh = (updated, _) =>
        {
            writerCalls++; File.WriteAllText(privatePath, updated.GetHtml()); return Task.FromResult(updated.Clone());
        };
        var session = (BookReviewSession)Activator.CreateInstance(typeof(BookReviewSession), BindingFlags.Instance | BindingFlags.NonPublic,
            null, [privatePath, "html", "Многострочный текст", document, refresh], null)!;
        var localizer = new LocalizationService(); localizer.Load("ru");
        var review = new BookConversionReviewWindow(owner, session, localizer.T);
        var watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        watchdog.Tick += (_, _) => { failure ??= new TimeoutException("No-op multiline test timed out."); review.Close(); };
        review.Loaded += async (_, _) =>
        {
            try
            {
                _ = review.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        var editor = Application.Current.Windows.OfType<BookDocumentEditorWindow>().Single();
                        var text = Find<TextBox>(editor, "BookConverter.Editor.Text"); text.Focus();
                        Require(text.Text.Contains('\n'), "The selected no-op test block must actually be multiline.");
                        // Opening/focusing/reading the editor is intentional; no keyboard or text modification.
                        Click(editor, "BookConverter.Editor.Apply");
                    }
                    catch (Exception ex) { failure = ex; review.Close(); }
                }), DispatcherPriority.Background);
                Click(review, "BookConverter.Review.Edit");
                var until = DateTime.UtcNow.AddSeconds(10);
                while (!Find<Button>(review, "BookConverter.Review.Save").IsEnabled)
                { if (DateTime.UtcNow > until) throw new TimeoutException("No-op preview did not become savable."); await Task.Delay(15); }
                Require(session.Revision == 0, "Opening the multiline editor without changes must not create a revision.");
                Require(writerCalls == 0, "No-op Apply must not invoke the artifact writer.");
                Require(before.SequenceEqual(File.ReadAllBytes(privatePath)), "No-op Apply must leave private target bytes unchanged.");
                Click(review, "BookConverter.Review.Save");
            }
            catch (Exception ex) { failure = ex; review.Close(); }
        };
        watchdog.Start(); review.ShowDialog(); watchdog.Stop();
        if (failure is not null) throw failure;
        Require(review.Decision == BookReviewDecision.Save && session.Revision == 0 && writerCalls == 0, "No-op multiline Save changed the review state.");
        results.Add(new { scenario = "multiline editor no-op Apply and Save", passed = true, revision = session.Revision, writerCalls, privateBytesUnchanged = before.SequenceEqual(File.ReadAllBytes(privatePath)) });
        File.Delete(privatePath);
    }

    private static BookReviewDocument Document(string? html = null)
    {
        var assembly = typeof(BookReviewDocument).Assembly;
        var contentType = assembly.GetType("AIHub.Services.Books.BookContent")!;
        var content = Activator.CreateInstance(contentType, true)!;
        contentType.GetProperty("Title")!.SetValue(content, "Synthetic preview book");
        var partType = assembly.GetType("AIHub.Services.Books.BookPart")!;
        var parts = (IList)contentType.GetProperty("Parts")!.GetValue(content)!;
        const string cover = "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" viewBox=\"0 0 32 32\"><image xlink:href=\"asset0.jpg\" width=\"32\" height=\"32\"/></svg>";
        parts.Add(Activator.CreateInstance(partType, ["Chapter", cover + (html ?? "<p>Original <strong>formatted</strong> text.</p><table><tr><td>Cell</td></tr></table>")]));
        using var bitmap = new SKBitmap(32, 32); bitmap.Erase(SKColors.RoyalBlue);
        using var image = SKImage.FromBitmap(bitmap); using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        ((IDictionary)contentType.GetProperty("Assets")!.GetValue(content)!).Add("asset0.jpg", encoded.ToArray());
        return (BookReviewDocument)typeof(BookReviewDocument).GetMethod("FromContent", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [content, null])!;
    }
    private static void Click(DependencyObject root, string id)
    {
        var button = Find<Button>(root, id); Require(button.IsEnabled, "Button is disabled: " + id);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject
    {
        if (root is T match && AutomationProperties.GetAutomationId(root) == id) return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            try { return Find<T>(VisualTreeHelper.GetChild(root, i), id); }
            catch (KeyNotFoundException) { }
        }
        throw new KeyNotFoundException(id);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static IEnumerable<System.Windows.Controls.Image> Images(DependencyObject root)
    {
        if (root is System.Windows.Controls.Image image) yield return image;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Images(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
    private static void Capture(Window window, string name)
    {
        var evidence = Environment.GetEnvironmentVariable("LOPATA_BOOK_UI_EVIDENCE");
        if (string.IsNullOrWhiteSpace(evidence)) return;
        Directory.CreateDirectory(evidence);
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(window.ActualWidth)), Math.Max(1, (int)Math.Ceiling(window.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(evidence, name)); encoder.Save(stream);
    }
    private static void Theme(FrameworkElement root)
    {
        void Brush(string key, string value) => root.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        Brush("WindowBackgroundBrush", "#111827"); Brush("PanelBrush", "#172033");
        Brush("TextPrimaryBrush", "#F8FAFC"); Brush("TextSecondaryBrush", "#AAB4C4");
        Brush("InputBrush", "#111827"); Brush("SecondaryButtonBackgroundBrush", "#111827");
        Brush("StepBadgeBrush", "#1E3A5F"); Brush("LineBrush", "#2D374B"); Brush("AccentBrush", "#2563EB");
        root.Resources["UiBodyFontSize"] = 14d;
    }
}
