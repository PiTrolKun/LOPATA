using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Controls.LivingArcade;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.LivingArcade;
using Button = System.Windows.Controls.Button;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class LivingArcadeGameHudPreparationTests
{
    [TestMethod]
    [DataRow("ru")]
    [DataRow("en")]
    public Task PreparationIconPausesAndResumesActualWorkWhileTheGameIsPaused(string language) => ScenarioNavigationTests.Sta(() =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "arcade-hud-preparation-" + Guid.NewGuid().ToString("N"));
        var previous = ApplicationBackgroundOperations.Current;
        using var lifetime = new CancellationTokenSource();
        Task<int>? work = null;
        try
        {
            var controller = new BackgroundOperationController(new BackgroundOperationStore(Path.Combine(directory, "background", "state.json")));
            ApplicationBackgroundOperations.Current = controller;
            var store = new LivingArcadeRunStore(Path.Combine(directory, "runs"));
            var localizer = new LocalizationService(); localizer.Load(language);
            var paths = new LivingArcadeModelPaths("never-start-main.gguf", "never-start-artist.gguf");
            using var generation = new LivingArcadeGeneration(new UserContextService(new(), new()));
            using var page = new LivingArcadeControl(localizer.T, store, generation, paths, () => language, default);
            LivingArcadeGameHudTests.StartFixtureRun(page, store);
            var window = LivingArcadeGameHudTests.WindowFor(page, 1024, 700);
            try
            {
                window.Show(); window.UpdateLayout();
                var icon = ScenarioNavigationTests.LogicalDescendants(page).OfType<Button>().Single(button =>
                    button.IsVisible && AutomationProperties.GetAutomationId(button) == "Arcade.PauseGeneration");
                Assert.IsFalse(icon.IsEnabled, "Preparation controls must be disabled when this run has no pending work.");
                var pauseGame = ScenarioNavigationTests.LogicalDescendants(page).OfType<Button>().Single(button =>
                    button.IsVisible && AutomationProperties.GetAutomationId(button) == "Arcade.PauseGame");
                pauseGame.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var host = ScenarioNavigationTests.LogicalDescendants(page).OfType<LivingArcadeGameHost>().Single();
                Assert.IsFalse(host.IsRunning);
                Assert.IsTrue(host.Snapshot!.IsPaused);

                var runId = store.List().Single().Id;
                var state = new BackgroundOperationState
                {
                    Kind = LivingArcadeGeneration.Kind, Title = "HUD preparation fixture", Project = runId,
                    Input = JsonSerializer.SerializeToElement(new LivingArcadeGenerationRequest(runId, 1, paths, language))
                };
                var attempts = 0; var releases = 0;
                work = controller.RunAsync(state, async token =>
                {
                    var attempt = Interlocked.Increment(ref attempts);
                    controller.SaveCheckpoint(new { attempt, sentinel = 17 });
                    await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                    return attempt;
                }, () => { Interlocked.Increment(ref releases); return Task.CompletedTask; }, lifetime.Token);
                PumpUntil(() => icon.IsEnabled && AutomationProperties.GetAutomationId(icon) == "Arcade.PauseGeneration");
                Assert.AreEqual(localizer.T("Arcade.PauseGeneration"), icon.ToolTip);
                Assert.AreEqual(BackgroundOperationPhase.Running, controller.State!.Phase);
                icon.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => controller.State?.Phase == BackgroundOperationPhase.Paused
                    && AutomationProperties.GetAutomationId(icon) == "Arcade.ResumeGeneration");
                Assert.IsTrue(controller.State!.UserPaused);
                Assert.AreEqual(1, Volatile.Read(ref releases), "The worker must retire before the icon advertises a paused operation.");
                Assert.AreEqual(17, controller.State.Checkpoint!.Value.GetProperty("sentinel").GetInt32());
                Assert.AreEqual(localizer.T("Arcade.ResumeGeneration"), icon.ToolTip);
                Assert.AreEqual(localizer.T("Arcade.ResumeGeneration"), AutomationProperties.GetName(icon));
                icon.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => Volatile.Read(ref attempts) == 2 && controller.State?.Phase == BackgroundOperationPhase.Running
                    && AutomationProperties.GetAutomationId(icon) == "Arcade.PauseGeneration");
                Assert.IsFalse(controller.State!.UserPaused);
                Assert.AreEqual(1, Volatile.Read(ref releases));
                Assert.AreEqual(2, controller.State.Checkpoint!.Value.GetProperty("attempt").GetInt32());
                Assert.AreEqual(localizer.T("Arcade.PauseGeneration"), icon.ToolTip);
                Assert.IsFalse(host.IsRunning, "Resuming preparation must not resume the paused game.");
                Assert.IsTrue(host.Snapshot!.IsPaused);
                Assert.IsNull(generation.RuntimeSession, "This fixture must never prepare or start a model.");
            }
            finally
            {
                lifetime.Cancel();
                if (work is not null)
                {
                    PumpUntil(() => work.IsCompleted);
                    try { work.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
                }
                window.Close();
            }
        }
        finally
        {
            ApplicationBackgroundOperations.Current = previous;
            var full = Path.GetFullPath(directory);
            if (Directory.Exists(full) && full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(full).StartsWith("arcade-hud-preparation-", StringComparison.Ordinal)) Directory.Delete(full, true);
        }
    });

    private static void PumpUntil(Func<bool> ready)
    {
        if (ready()) return;
        var deadline = DateTime.UtcNow.AddSeconds(5); var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (ready() || DateTime.UtcNow >= deadline) frame.Continue = false; };
        timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        Assert.IsTrue(ready(), "The actual preparation operation and its icon did not reach the expected state.");
    }
}
