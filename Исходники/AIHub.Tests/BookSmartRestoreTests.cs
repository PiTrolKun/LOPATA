using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.Books;
using AIHub.Services.Books.Smart;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ListView = System.Windows.Controls.ListView;
using Brushes = System.Windows.Media.Brushes;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class BookSmartRestoreTests
{
    [TestMethod]
    public void MarkdownImageProbeReadsRealMarkupInsteadOfAssumingAllMarkdownIsTextOnly()
    {
        var root = Path.Combine(Path.GetTempPath(), "LOPATA-smart-probe-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source.md");
            File.WriteAllText(source, "# Глава\n\n![Иллюстрация](picture.png)\n\nТекст автора.");
            var original = File.ReadAllBytes(source);
            Assert.IsTrue(BookConversionWindow.MayContainImages(source)); CollectionAssert.AreEqual(original, File.ReadAllBytes(source));
            File.WriteAllText(source, "# Глава\n\n```text\n![Это пример кода](picture.png)\n```\n\nОбычный текст.");
            Assert.IsFalse(BookConversionWindow.MayContainImages(source), "A quoted code example must not load visual models.");
            File.WriteAllText(source, "Авторский текст.\n\n<img src='picture.png' alt='Иллюстрация'>");
            Assert.IsTrue(BookConversionWindow.MayContainImages(source));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod, DataRow(false), DataRow(true)]
    public Task RestoringCapturedSourcesReplacesForeignRowsAndMapsReceiptsIncludingSkippedAndMergedGroups(bool merge) => Sta(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "LOPATA-smart-restore-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var sources = new[] { "two.txt", "one.txt", "three.txt" }.Select(name => Path.Combine(root, name)).ToArray();
        foreach (var source in sources) File.WriteAllText(source, "Author text from " + Path.GetFileName(source));
        var foreign = Path.Combine(root, "foreign.txt"); File.WriteAllText(foreign, "Unrelated existing selection");
        var request = new BookConversionRequest { Sources = sources, Smart = new("ovis", "beta"), OutputFormat = "html", OutputDirectory = Path.Combine(root, "out"), Merge = merge, Title = merge ? "Combined book" : null };
        var input = await BookSmartWork.CaptureAsync(request, Path.Combine(root, "models"), Path.Combine(root, "core.gguf"), "ru", CancellationToken.None);
        var owner = Owner(); var localization = new LocalizationService(); localization.Load("ru");
        var window = new BookConversionWindow(owner, localization.T);
        try
        {
            var work = new BookSmartWork();
            var result = await work.RunAsync(input, _ => new Passthrough(), (session, _) => Task.FromResult(!merge && session.SourceName == "one.txt" ? BookReviewDecision.Skip : BookReviewDecision.Save), null, CancellationToken.None);
            Assert.AreEqual(merge ? 1 : 2, result.Outputs.Length);
            window.Show(); await Invoke(window, "AddPathsAsync", (object)new[] { foreign });
            var files = Find<ListView>(window, "Files"); Assert.AreEqual(1, files.Items.Count);
            files.Items[0].GetType().GetProperty("IsChecked")!.SetValue(files.Items[0], false);
            await Invoke(window, "RestoreSmartEntries", input, result); await Render(window);
            CollectionAssert.AreEqual(sources, files.Items.Cast<object>().Select(row => Value<string>(row, "Path")).ToArray());
            Assert.IsTrue(files.Items.Cast<object>().All(row => Value<bool>(row, "IsChecked")));
            Assert.AreEqual(true, Find<CheckBox>(window, "CheckAll").IsChecked);
            if (merge)
                Assert.IsTrue(files.Items.Cast<object>().All(row => Value<string>(row, "Output") == result.Outputs[0]));
            else
            {
                Assert.AreEqual(result.Outputs[0], Value<string>(files.Items[0], "Output"));
                Assert.AreEqual("", Value<string>(files.Items[1], "Output"));
                Assert.AreEqual(localization.T("BookConverter.SkippedShort"), Value<string>(files.Items[1], "Status"));
                Assert.AreEqual(result.Outputs[1], Value<string>(files.Items[2], "Output"));
            }
            Assert.IsTrue(Find<Button>(window, "OpenResult").IsEnabled);
            Assert.AreEqual(merge, Find<CheckBox>(window, "Merge").IsChecked);
        }
        finally { window.Close(); owner.Close(); DeleteOwnedRun(input.Id); Directory.Delete(root, true); }
    });

    [TestMethod]
    public Task ResumeHonorsAnAlreadyCancelledTokenAndBusyGuardWithoutStartingModels() => Sta(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "LOPATA-smart-resume-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var source = Path.Combine(root, "captured.txt"); var foreign = Path.Combine(root, "foreign.txt");
        File.WriteAllText(source, "Captured source"); File.WriteAllText(foreign, "Unrelated row");
        var input = await BookSmartWork.CaptureAsync(new() { Sources = [source], Smart = new(), OutputFormat = "html", OutputDirectory = Path.Combine(root, "out") },
            Path.Combine(root, "not-installed-models"), Path.Combine(root, "absent-core.gguf"), "ru", CancellationToken.None);
        var state = new BackgroundOperationState { Kind = BookSmartWork.Kind, Title = "Resume fixture", Input = JsonSerializer.SerializeToElement(input) };
        var owner = Owner(); var window = new BookConversionWindow(owner, key => key);
        try
        {
            window.Show(); await Invoke(window, "AddPathsAsync", (object)new[] { foreign });
            var busy = typeof(BookConversionWindow).GetField("_busy", BindingFlags.Instance | BindingFlags.NonPublic)!;
            busy.SetValue(window, true);
            await Assert.ThrowsAsync<BackgroundOperationWaitingException>(() => window.ResumeSmartAsync(state, CancellationToken.None));
            Assert.AreEqual(foreign, Value<string>(Find<ListView>(window, "Files").Items[0], "Path"), "A busy window must not overwrite its existing sources.");
            busy.SetValue(window, false);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => window.ResumeSmartAsync(state, cancelled.Token));
            Assert.IsFalse((bool)busy.GetValue(window)!);
            Assert.IsNull(typeof(BookConversionWindow).GetField("_work", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
            Assert.AreEqual(source, Value<string>(Find<ListView>(window, "Files").Items[0], "Path"));
            Assert.IsFalse(Directory.Exists(Path.Combine(BookSmartWork.DirectoryPath, "runs", input.Id)), "Cancelled resume must not enter the worker or load absent models.");
            Assert.IsFalse(Directory.Exists(input.Request.OutputDirectory));
        }
        finally { window.Close(); owner.Close(); DeleteOwnedRun(input.Id); Directory.Delete(root, true); }
    });

    private sealed class Passthrough : IBookSmartProcessor
    { public Task<BookSmartProcessed> ProcessAsync(BookContent content, string source, CancellationToken token) => Task.FromResult(new BookSmartProcessed(content, [], [])); }
    private static T Value<T>(object row, string name) => (T)row.GetType().GetProperty(name)!.GetValue(row)!;
    private static async Task Invoke(object owner, string name, params object[] args)
    { var value = owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, args); if (value is Task task) await task; }
    private static T Find<T>(DependencyObject root, string suffix) where T : DependencyObject => Desc<T>(root).Single(value => AutomationProperties.GetAutomationId(value) == "BookConverter." + suffix);
    private static IEnumerable<T> Desc<T>(DependencyObject root) where T : DependencyObject
    { for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++) { var child = VisualTreeHelper.GetChild(root, index); if (child is T value) yield return value; foreach (var nested in Desc<T>(child)) yield return nested; } }
    private static Window Owner()
    {
        var owner = new Window();
        foreach (var key in new[] { "TextPrimaryBrush", "TextSecondaryBrush", "LineBrush", "AccentBrush" }) owner.Resources[key] = Brushes.Black;
        foreach (var key in new[] { "PanelBrush", "WindowBackgroundBrush", "InputBrush", "SecondaryButtonBackgroundBrush", "StepBadgeBrush" }) owner.Resources[key] = Brushes.White;
        owner.Resources["UiBodyFontSize"] = 14d; owner.Show(); return owner;
    }
    private static void DeleteOwnedRun(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Unexpected test run ID.");
        var parent = Path.GetFullPath(Path.Combine(BookSmartWork.DirectoryPath, "runs")); var target = Path.GetFullPath(Path.Combine(parent, id));
        if (!target.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unexpected test path.");
        if (Directory.Exists(target)) Directory.Delete(target, true);
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
}
