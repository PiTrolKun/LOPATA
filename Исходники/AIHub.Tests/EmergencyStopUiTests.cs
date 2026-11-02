using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Xml.Linq;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using Path = System.IO.Path;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class EmergencyStopUiTests
{
    [TestMethod, DataRow("ru"), DataRow("en")]
    public Task ReviewingCancelingAndConfirmingDoNotExecuteTheEmergencyAction(string language) => OnUi(async () =>
    {
        using var files = new MusicProjectTests.Files();
        var controller = new BackgroundOperationController(new BackgroundOperationStore(Path.Combine(files.Root, "operation.json")));
        var completedFile = Path.Combine(files.Root, "completed-stage.txt");
        System.IO.Directory.CreateDirectory(files.Root);
        System.IO.File.WriteAllText(completedFile, "Completed result must survive review.");
        var pending = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var retired = 0; CancellationToken workerToken = default;
        var work = controller.RunAsync(new BackgroundOperationState { Kind = "test.review", Title = "Reviewed work",
            Input = System.Text.Json.JsonSerializer.SerializeToElement(new { prompt = "Reviewed input" }) },
            token => { workerToken = token; return pending.Task; },
            () => { retired++; return Task.CompletedTask; }, default);
        var localizer = new LocalizationService(); localizer.Load(language);
        try
        {
            foreach (var decision in new[] { "cancel", "close", "confirm" })
            {
                var dialog = Dialog(localizer.T, ["Reviewed work · test.review · Running"], ["PID 1234 · test-worker"]);
                var result = ShowAndDrive(dialog, () =>
                {
                    Assert.IsNull(dialog.DialogResult, "Merely opening the review must not confirm it.");
                    Assert.IsTrue(controller.IsRunning);
                    Assert.IsFalse(workerToken.IsCancellationRequested);
                    Assert.AreEqual(0, retired);
                    if (decision == "close") dialog.Close();
                    else Find<Button>(dialog, decision == "confirm" ? "EmergencyStop.Confirm" : "EmergencyStop.Cancel")
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                });
                Assert.AreEqual(decision == "confirm", result == true);
                Assert.IsTrue(controller.IsRunning, "The dialog returns consent; the caller owns execution.");
                Assert.IsFalse(workerToken.IsCancellationRequested);
                Assert.AreEqual(0, retired);
                Assert.AreEqual("Completed result must survive review.", System.IO.File.ReadAllText(completedFile));
            }
        }
        finally
        {
            pending.TrySetResult(42);
            Assert.AreEqual(42, await work.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    });

    [TestMethod, DataRow("ru", true), DataRow("ru", false), DataRow("en", true), DataRow("en", false)]
    public Task LongReviewRemainsReadableAndItsSafeKeyboardActionAndFooterStayReachable(string language, bool dark) => OnUi(() =>
    {
        var localizer = new LocalizationService(); localizer.Load(language);
        var tasks = Enumerable.Range(1, 36).Select(n => $"task-{n:D2} · Song → clip · Paused · preserve completed stage {n}").ToArray();
        var workers = Enumerable.Range(1, 24).Select(n => $"PID {1200 + n} · owned-worker-{n:D2} · 2026-10-31T00:00:00Z").ToArray();
        var dialog = Dialog(localizer.T, tasks, workers);
        dialog.Width = 520; dialog.Height = 420;
        Theme(dialog, dark);
        var result = ShowAndDrive(dialog, () =>
        {
            dialog.UpdateLayout();
            Assert.AreEqual(localizer.T("EmergencyStop.Title"), dialog.Title);
            Assert.IsFalse(dialog.Title.StartsWith("EmergencyStop.", StringComparison.Ordinal));
            var texts = ScenarioNavigationTests.LogicalDescendants(dialog).OfType<TextBlock>().Select(t => t.Text).ToArray();
            Assert.IsFalse(texts.Any(t => t.StartsWith("EmergencyStop.", StringComparison.Ordinal)), "Every visible review label must resolve in RU and EN.");
            foreach (var key in new[] { "Intro", "Preserve", "Tasks", "Workers" })
                CollectionAssert.Contains(texts, localizer.T("EmergencyStop." + key));
            CollectionAssert.AreEqual(tasks, ListText(dialog, "EmergencyStop.Tasks"));
            CollectionAssert.AreEqual(workers, ListText(dialog, "EmergencyStop.Workers"));
            var cancel = Find<Button>(dialog, "EmergencyStop.Cancel");
            var confirm = Find<Button>(dialog, "EmergencyStop.Confirm");
            Assert.IsTrue(cancel.IsCancel && cancel.IsDefault, "Enter and Escape must retain the safe action unless the danger action is deliberately chosen.");
            Assert.IsFalse(confirm.IsDefault);
            Assert.AreEqual(localizer.T("EmergencyStop.Cancel"), ((TextBlock)cancel.Content).Text);
            Assert.AreEqual(localizer.T("EmergencyStop.Confirm"), ((TextBlock)confirm.Content).Text);
            Assert.AreEqual(Colors.White, ((SolidColorBrush)((TextBlock)confirm.Content).Foreground).Color);
            var danger = ((SolidColorBrush)confirm.Background).Color;
            Assert.IsTrue(danger.R > 120 && danger.G < 90 && danger.B < 90, "The confirmation remains visibly red in either theme.");
            var scroll = ScenarioNavigationTests.LogicalDescendants(dialog).OfType<ScrollViewer>().Single();
            Assert.IsTrue(scroll.ScrollableHeight > 0, "A long exact process list must scroll instead of hiding actions.");
            scroll.ScrollToEnd(); dialog.UpdateLayout();
            Assert.IsTrue(scroll.VerticalOffset > 0);
            var root = (FrameworkElement)dialog.Content;
            foreach (var action in new[] { cancel, confirm })
            {
                var at = action.TranslatePoint(new Point(), root);
                Assert.IsTrue(at.Y >= 0 && at.Y + action.ActualHeight <= root.ActualHeight + 1,
                    "Review actions stay visible while the list scrolls.");
            }
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
        Assert.AreEqual(false, result);
        return Task.CompletedTask;
    });

    [TestMethod, DataRow("ru"), DataRow("en")]
    public Task EmptyReviewExplainsBothEmptyGroupsWithoutInventingItems(string language) => OnUi(() =>
    {
        var localizer = new LocalizationService(); localizer.Load(language);
        var dialog = Dialog(localizer.T, [], []);
        ShowAndDrive(dialog, () =>
        {
            CollectionAssert.AreEqual(new[] { localizer.T("EmergencyStop.NoTasks") }, ListText(dialog, "EmergencyStop.Tasks"));
            CollectionAssert.AreEqual(new[] { localizer.T("EmergencyStop.NoWorkers") }, ListText(dialog, "EmergencyStop.Workers"));
            Assert.AreNotEqual("EmergencyStop.NoTasks", localizer.T("EmergencyStop.NoTasks"));
            Assert.AreNotEqual("EmergencyStop.NoWorkers", localizer.T("EmergencyStop.NoWorkers"));
            Find<Button>(dialog, "EmergencyStop.Cancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task HeaderStopSignIsAnOctagonBeforeProfileAndFollowsTheUiScale() => OnUi(() =>
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var source = XDocument.Load(MainWindowSource());
        var sign = source.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "EmergencyStopButton");
        var profile = source.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "ProfileButton");
        Assert.AreSame(sign.Parent, profile.Parent);
        Assert.AreEqual(profile, sign.ElementsAfterSelf().First(), "The emergency sign belongs immediately before the profile action.");
        Assert.AreEqual("EmergencyStopButton", (string?)sign.Attribute("AutomationProperties.AutomationId"));
        var standalone = new XElement(sign);
        standalone.Attribute("Click")?.Remove(); // Render just the sign; never construct MainWindow or invoke startup.
        standalone.SetAttributeValue(XNamespace.Xmlns + "x", x.NamespaceName);
        var button = (Button)XamlReader.Parse(standalone.ToString());
        button.Resources["AccentBrush"] = Brushes.RoyalBlue;
        foreach (var size in new[] { 42d, 63d })
        {
            button.Resources["UiThemeButtonSize"] = size;
            button.Measure(new Size(100, 100)); button.Arrange(new Rect(0, 0, size, size)); button.ApplyTemplate(); button.UpdateLayout();
            Assert.AreEqual(size, button.ActualWidth); Assert.AreEqual(size, button.ActualHeight);
            var face = (Polygon)button.Template.FindName("StopFace", button);
            Assert.HasCount(8, face.Points);
            Assert.AreEqual(Colors.White, ((SolidColorBrush)face.Stroke).Color);
            var red = ((SolidColorBrush)face.Fill).Color;
            Assert.IsTrue(red.R > 120 && red.G < 90 && red.B < 90);
            Assert.IsTrue(VisualDescendants(button).OfType<TextBlock>().Any(t => t.Text == "STOP" && ((SolidColorBrush)t.Foreground).Color == Colors.White));
        }
        return Task.CompletedTask;
    });

    private static EmergencyStopWindow Dialog(Func<string, string> localize, IReadOnlyList<string> tasks, IReadOnlyList<string> workers) =>
        new(localize, tasks, workers) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };

    private static bool? ShowAndDrive(EmergencyStopWindow dialog, Action drive)
    {
        Exception? failure = null;
        dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            try { drive(); }
            catch (Exception error) { failure = error; dialog.Close(); }
        }));
        try
        {
            var result = dialog.ShowDialog();
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            return result;
        }
        finally { dialog.Close(); }
    }

    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject => ScenarioNavigationTests.LogicalDescendants(root)
        .OfType<T>().Single(e => AutomationProperties.GetAutomationId(e) == id);
    private static string[] ListText(DependencyObject root, string id) => ScenarioNavigationTests.LogicalDescendants(Find<StackPanel>(root, id))
        .OfType<TextBlock>().Select(t => t.Text).ToArray();
    private static void Theme(Window dialog, bool dark)
    {
        var resources = ScenarioNavigationTests.Create("ru", 1, dark).Resources;
        foreach (var key in resources.Keys) dialog.Resources[key] = resources[key];
        dialog.Resources["WindowBackgroundBrush"] = dark ? Brushes.Black : Brushes.White;
        dialog.Resources["SecondaryButtonBackgroundBrush"] = dark ? Brushes.DimGray : Brushes.WhiteSmoke;
    }
    private static string MainWindowSource([CallerFilePath] string file = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", "AIHub", "MainWindow.xaml"));
    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in VisualDescendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static Task OnUi(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); done.SetResult(); }
                catch (Exception error) { done.SetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return done.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
