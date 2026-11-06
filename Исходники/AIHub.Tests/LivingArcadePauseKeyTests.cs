using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using AIHub.Controls;
using AIHub.Controls.LivingArcade;
using AIHub.Services;
using AIHub.Services.LivingArcade;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using RichTextBox = System.Windows.Controls.RichTextBox;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class LivingArcadePauseKeyTests
{
    [TestMethod, DataRow("ru"), DataRow("en")]
    public Task SpacePausesAndResumesExactlyOncePerPressWithoutRendering(string language) => OnPage(language, (page, host, _) =>
    {
        AssertState(page, host, false);
        Assert.IsTrue(Press(page, host));
        AssertState(page, host, true);
        Assert.IsTrue(Press(page, host)); // A duplicate key-down without a release is still the same press.
        Assert.IsTrue(Press(page, host, repeat: true));
        AssertState(page, host, true);
        Assert.IsTrue(Press(page, host, up: true));
        Assert.IsTrue(Press(page, host));
        AssertState(page, host, false);
        Assert.IsTrue(Press(page, host, repeat: true));
        AssertState(page, host, false);
        Press(page, host, up: true);
        Press(page, host);
        AssertState(page, host, true);
        Press(page, host, up: true);
        FindButton(page, "Arcade.ResumeGame").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        AssertState(page, host, false);
        FindButton(page, "Arcade.PauseGame").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        AssertState(page, host, true);
        FindButton(page, "Arcade.PauseGame").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        AssertState(page, host, true); // Explicit Pause retains its idempotent meaning.
    });

    [TestMethod, DataRow("ru"), DataRow("en")]
    public Task SpaceDoesNotHijackOtherControlsModifiersMenusOrInactiveWindows(string language) => OnPage(language, (page, host, window) =>
    {
        foreach (var source in new DependencyObject[] { new TextBox(), new Slider(), new RichTextBox().Document,
            FindButton(page, "Arcade.ToMenu"), page })
        {
            Assert.IsFalse(Press(page, source));
            AssertState(page, host, false);
        }
        foreach (var modifier in new[] { ModifierKeys.Alt, ModifierKeys.Control, ModifierKeys.Shift, ModifierKeys.Windows })
        {
            Assert.IsFalse(Press(page, host, modifiers: modifier));
            AssertState(page, host, false);
        }
        Assert.IsFalse(Press(page, host, active: false));
        Assert.IsFalse(page.HandlePauseShortcut(Key.Escape, false, false, host, true, ModifierKeys.None));
        AssertState(page, host, false);
        Assert.IsFalse(window.IsActive, "The test must never activate a desktop window.");
        var routed = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(host)!, 0, Key.Space)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = host };
        host.RaiseEvent(routed);
        Assert.IsFalse(routed.Handled, "The real routed handler must respect the inactive-window guard.");
        AssertState(page, host, false);
        Set(page, "_transitioning", true);
        Assert.IsFalse(Press(page, host));
        Set(page, "_transitioning", false);
        page.Visibility = Visibility.Collapsed;
        Assert.IsFalse(Press(page, host));
        page.Visibility = Visibility.Visible;
        Set(page, "_playing", false);
        Assert.IsFalse(Press(page, host));
        Set(page, "_playing", true);
    });

    [TestMethod, DataRow("ru"), DataRow("en")]
    public Task FocusedPauseIconsCannotReceiveASpaceClickAndMenuReentryClearsTheLatch(string language) => OnPage(language, (page, host, _) =>
    {
        foreach (var id in new[] { "Arcade.PauseGame", "Arcade.ResumeGame" })
        {
            var button = FindButton(page, id);
            Assert.IsFalse(button.Focusable, "Clicking pause/resume must keep the field as the keyboard target.");
            Assert.IsTrue(Press(page, button));
            Assert.IsTrue(Press(page, (DependencyObject)button.Content));
            AssertState(page, host, false);
        }
        Press(page, host); // Leave the key latch down intentionally while navigating away.
        AssertState(page, host, true);
        FindButton(page, "Arcade.ToMenu").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.IsFalse(Press(page, host));
        var resumeRun = Descendants(page).OfType<Button>().Single(button =>
            AutomationProperties.GetAutomationId(button).StartsWith("Arcade.Continue.", StringComparison.Ordinal));
        resumeRun.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var nextHost = Descendants(page).OfType<LivingArcadeGameHost>().Single();
        Assert.AreNotSame(host, nextHost);
        AssertState(page, nextHost, false);
        Press(page, nextHost);
        AssertState(page, nextHost, true);
    });

    private static Task OnPage(string language, Action<LivingArcadeControl, LivingArcadeGameHost, Window> action) => ScenarioNavigationTests.Sta(() =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "arcade-pause-key-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LivingArcadeRunStore(directory);
            var localizer = new LocalizationService(); localizer.Load(language);
            using var generation = new LivingArcadeGeneration(new UserContextService(new(), new()));
            using var page = new LivingArcadeControl(localizer.T, store, generation,
                new("unused", "unused"), () => language, default);
            LivingArcadeGameHudTests.StartFixtureRun(page, store);
            var window = LivingArcadeGameHudTests.WindowFor(page, 1024, 700);
            try
            {
                window.Show(); window.UpdateLayout();
                var host = Descendants(page).OfType<LivingArcadeGameHost>().Single();
                // Establish the real page/host command state without activating the offscreen window.
                FindButton(page, "Arcade.PauseGame").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                FindButton(page, "Arcade.ResumeGame").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                action(page, host, window);
            }
            finally { window.Close(); }
        }
        finally
        {
            var full = Path.GetFullPath(directory);
            if (Directory.Exists(full) && full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(full).StartsWith("arcade-pause-key-", StringComparison.Ordinal)) Directory.Delete(full, true);
        }
    });

    private static bool Press(LivingArcadeControl page, DependencyObject source, bool repeat = false, bool up = false,
        bool active = true, ModifierKeys modifiers = ModifierKeys.None) =>
        page.HandlePauseShortcut(Key.Space, repeat, up, source, active, modifiers);

    private static void AssertState(LivingArcadeControl page, LivingArcadeGameHost host, bool paused)
    {
        Assert.AreEqual(paused, (bool)typeof(LivingArcadeControl).GetField("_paused", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!);
        Assert.AreEqual(paused, host.Snapshot!.IsPaused);
        Assert.AreEqual(!paused, host.IsRunning);
        Assert.AreEqual(!paused, FindButton(page, "Arcade.PauseGame").IsEnabled);
        Assert.AreEqual(paused, FindButton(page, "Arcade.ResumeGame").IsEnabled);
    }

    private static Button FindButton(LivingArcadeControl page, string id) => Descendants(page).OfType<Button>()
        .Single(button => button.IsVisible && AutomationProperties.GetAutomationId(button) == id);

    private static void Set(LivingArcadeControl page, string name, object value) =>
        typeof(LivingArcadeControl).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in Descendants(child)) yield return descendant;
    }
}
