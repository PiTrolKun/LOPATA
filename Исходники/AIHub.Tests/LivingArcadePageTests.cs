using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.LivingArcade;
using Button = System.Windows.Controls.Button;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class LivingArcadePageTests
{
    [TestMethod]
    [DataRow("ru")]
    [DataRow("en")]
    public Task HomeGamepadRoutesToPreparationAndThreeRunsStayIndependent(string language) => ScenarioNavigationTests.Sta(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-arcade-ui-" + Guid.NewGuid().ToString("N"));
        try
        {
            var nav = ScenarioNavigationTests.Create(language, 1, true);
            var requested = new List<string>(); nav.ScenarioRequested += requested.Add;
            var entrance = Descendants(nav).OfType<Button>().Single(b => Id(b) == "Arcade.Open");
            entrance.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            CollectionAssert.AreEqual(new[] { ScenarioNavigationCatalog.LivingArcade }, requested);
            Assert.IsNull(nav.LivingArcade, "A click must not bypass component preparation.");
            var store = new LivingArcadeRunStore(root);
            var runs = Enumerable.Range(1, 3).Select(n => store.Create("Run " + n, n)).ToArray();
            using var generation = new LivingArcadeGeneration(new UserContextService(new(), new()));
            var localizer = new LocalizationService(); localizer.Load(language);
            using var page = new LivingArcadeControl(localizer.T, store, generation, new("missing-main", "missing-artist"), () => language, default);
            nav.ShowLivingArcade(page);
            Assert.AreSame(page, nav.Content);
            var buttons = Descendants(page).OfType<Button>().ToArray();
            Assert.AreEqual(3, buttons.Count(b => Id(b).StartsWith("Arcade.Continue.")));
            Assert.IsFalse(buttons.Single(b => Id(b) == "Arcade.New").IsEnabled);
            buttons.Single(b => Id(b) == "Arcade.Continue." + runs[1].Id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var pending = typeof(LivingArcadeControl).GetField("_pendingStartId", BindingFlags.NonPublic | BindingFlags.Instance)!;
            Assert.AreEqual(runs[1].Id, pending.GetValue(page));
            Assert.IsTrue(nav.GoBack());
            Assert.IsNull(pending.GetValue(page), "Leaving the page must cancel delayed auto-start intent.");
            Assert.IsFalse(nav.IsLivingArcade);
            nav.ShowLivingArcade(page);
            Assert.AreSame(page, nav.Content);
            Assert.AreEqual(3, store.List().Count);
            Assert.AreEqual(0, store.List().Sum(r => r.History.Count));
        }
        finally
        {
            var full = Path.GetFullPath(root);
            if (full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(full).StartsWith("lopata-arcade-ui-", StringComparison.Ordinal)) Directory.Delete(full, true);
        }
    });

    [TestMethod]
    public Task PauseClickAfterFocusLossStaysPausedAndResumeIsExplicit() => ScenarioNavigationTests.Sta(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-arcade-ui-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LivingArcadeRunStore(root);
            var run = store.Create("Focus regression", 17);
            store.Update(run.Id, r => r.FirstEventsPrepared = true);
            using var generation = new LivingArcadeGeneration(new UserContextService(new(), new()));
            var localizer = new LocalizationService(); localizer.Load("en");
            using var page = new LivingArcadeControl(localizer.T, store, generation, new("missing-main", "missing-artist"), () => "en", default);
            void Set(string name, object value) => typeof(LivingArcadeControl).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, value);
            // Native inference belongs to a separate opt-in test; this test exercises the real field/button wiring.
            Set("_ready", true); Set("_generating", true);
            typeof(LivingArcadeControl).GetMethod("StartRun", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, [run.Id]);
            var host = (AIHub.Controls.LivingArcade.LivingArcadeGameHost)typeof(LivingArcadeControl)
                .GetField("_host", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!;
            host.GetType().GetMethod("RequestPause", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(host, null);
            var buttons = Descendants(page).OfType<Button>().ToArray();
            buttons.Single(b => Id(b) == "Arcade.PauseGame").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.IsFalse(host.IsRunning, "A Pause click must not undo the pause caused by its focus change.");
            Assert.IsTrue(host.Snapshot!.IsPaused);
            buttons.Single(b => Id(b) == "Arcade.ResumeGame").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.IsTrue(host.IsRunning);
            Set("_transitioning", true);
            buttons.Single(b => Id(b) == "Arcade.PauseGame").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            buttons.Single(b => Id(b) == "Arcade.ResumeGame").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.IsFalse(host.IsRunning, "Resume must not restart a finishing attempt.");
        }
        finally
        {
            var full = Path.GetFullPath(root);
            if (full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(full).StartsWith("lopata-arcade-ui-", StringComparison.Ordinal)) Directory.Delete(full, true);
        }
    });

    [TestMethod]
    public Task ReopeningPendingPreparationShowsResumeWithoutStartingModels() => ScenarioNavigationTests.Sta(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-arcade-ui-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LivingArcadeRunStore(root);
            using var generation = new LivingArcadeGeneration(new UserContextService(new(), new()));
            var localizer = new LocalizationService(); localizer.Load("ru");
            using var page = new LivingArcadeControl(localizer.T, store, generation,
                new("missing-main", "missing-artist"), () => "ru", default);
            var state = new AIHub.Models.BackgroundOperationState {
                Kind = LivingArcadeGeneration.FirstEventsKind, Title = "Pending first events",
                Phase = AIHub.Models.BackgroundOperationPhase.Waiting,
                UserPaused = true, RequiresDecision = true };
            page.ShowPendingOperation(state);
            var nav = ScenarioNavigationTests.Create("ru", 1, true);
            nav.ShowLivingArcade(page); Assert.IsTrue(nav.GoBack()); nav.ShowLivingArcade(page);
            page.ShowPendingOperation(state);
            Assert.IsTrue(Descendants(page).OfType<Button>().Single(b => Id(b) == "Arcade.ResumeGeneration").IsEnabled);
            Assert.IsNull(generation.RuntimeSession);
            Assert.IsTrue(state.UserPaused);
            Assert.AreEqual(AIHub.Models.BackgroundOperationPhase.Waiting, state.Phase);
            Assert.AreEqual(0, store.List().Count);
        }
        finally
        {
            var full = Path.GetFullPath(root);
            if (full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(full).StartsWith("lopata-arcade-ui-", StringComparison.Ordinal)) Directory.Delete(full, true);
        }
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task ReentryControlsShownWorkUntilUserExplicitlyChoosesAnotherRun(bool chooseRunAgain) => ScenarioNavigationTests.Sta(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-arcade-ui-" + Guid.NewGuid().ToString("N"));
        var previous = ApplicationBackgroundOperations.Current;
        try
        {
            var store = new LivingArcadeRunStore(Path.Combine(root, "runs"));
            var first = store.Create("A"); var second = store.Create("B");
            var paths = new LivingArcadeModelPaths("missing-main", "missing-artist");
            BackgroundOperationState Work(string runId) => new() {
                Kind = LivingArcadeGeneration.FirstEventsKind, Title = "Pending " + runId,
                Phase = BackgroundOperationPhase.Waiting, UserPaused = true,
                Input = JsonSerializer.SerializeToElement(new LivingArcadeFirstEventsRequest(runId, paths, "ru")) };
            var a = Work(first.Id); var b = Work(second.Id);
            var backgroundStore = new BackgroundOperationStore(Path.Combine(root, "background", "state.json"));
            backgroundStore.Operation(a.Id).Save(a); backgroundStore.Operation(b.Id).Save(b); backgroundStore.Save(b);
            var controller = new BackgroundOperationController(backgroundStore); controller.Load();
            ApplicationBackgroundOperations.Current = controller;
            var resumed = new List<string>();
            controller.Register(LivingArcadeGeneration.FirstEventsKind, (state, _) => {
                resumed.Add(state.Id); controller.DiscardPending(state.Id); return Task.CompletedTask;
            });
            using var generation = new LivingArcadeGeneration(new UserContextService(new(), new()));
            var localizer = new LocalizationService(); localizer.Load("ru");
            using var page = new LivingArcadeControl(localizer.T, store, generation, paths, () => "ru", default);
            void StartSecond() => typeof(LivingArcadeControl).GetMethod("StartRun", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, [second.Id]);
            void Click(string id) => Descendants(page).OfType<Button>().Single(button => Id(button) == id)
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            StartSecond(); // No warmup: retain B as the game selection without starting any model.
            var nav = ScenarioNavigationTests.Create("ru", 1, true);
            nav.ShowLivingArcade(page); Assert.IsTrue(nav.GoBack()); nav.ShowLivingArcade(page);
            page.ShowPendingOperation(a);
            Assert.AreEqual(second.Id, typeof(LivingArcadeControl).GetField("_runId", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page),
                "Showing A's preparation must not replace B's gameplay identity.");
            if (chooseRunAgain) StartSecond();
            var target = chooseRunAgain ? b : a;
            var other = chooseRunAgain ? a : b;
            Click("Arcade.PauseGeneration");
            Assert.AreEqual(BackgroundOperationPhase.Paused, controller.PendingOperations.Single(state => state.Id == target.Id).Phase);
            Assert.AreEqual(BackgroundOperationPhase.Waiting, controller.PendingOperations.Single(state => state.Id == other.Id).Phase);
            Click("Arcade.ResumeGeneration");
            CollectionAssert.AreEqual(new[] { target.Id }, resumed);
            Assert.IsNull(generation.RuntimeSession);
            if (!chooseRunAgain)
            {
                Click("Arcade.ResumeGeneration"); // Completed A no longer overrides the current B selection.
                CollectionAssert.AreEqual(new[] { a.Id, b.Id }, resumed);
            }
        }
        finally
        {
            ApplicationBackgroundOperations.Current = previous;
            var full = Path.GetFullPath(root);
            if (full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(full).StartsWith("lopata-arcade-ui-", StringComparison.Ordinal) && Directory.Exists(full)) Directory.Delete(full, true);
        }
    });

    private static string Id(DependencyObject value) => AutomationProperties.GetAutomationId(value);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root) => ScenarioNavigationTests.LogicalDescendants(root);
}
