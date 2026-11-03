using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class LetItOutChatPreparationTests
{
    [TestMethod]
    [DataRow("Retry", "retry")]
    [DataRow("Continue", "hybrid")]
    [DataRow("Cancel", "cancel")]
    public Task MemoryDialogReturnsOnlyTheExplicitChoice(string buttonKey, string expected) => Sta(async () =>
    {
        var owner = Owner();
        var window = new LetItOutMemoryDecisionWindow(owner, key => key, "Fixture: GPU capacity permits a full load; memory is busy.");
        try
        {
            window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(() => {
                Desc<Button>(window).Single(b => AutomationProperties.GetAutomationId(b) == "LetItOut.Memory." + buttonKey)
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            window.ShowDialog();
            Assert.AreEqual(expected, window.Choice);
            await Task.CompletedTask;
        }
        finally { window.Close(); owner.Close(); }
    });

    [TestMethod]
    public Task EntryOnlyVerifiesAndReadyStateWaitsForExplicitContinue() => Sta(async () =>
    {
        var owner = Owner(); var calls = new List<bool>();
        var window = Create(owner, (download, _, _) => { calls.Add(download); return Task.FromResult(Ready()); });
        try
        {
            window.Show(); await Render(window);
            CollectionAssert.AreEqual(new[] { false }, calls);
            Assert.IsTrue(window.IsVisible);
            Assert.IsNull(window.CoreModelPath);
            Assert.IsTrue(Action(window, "Continue").IsEnabled);
            Assert.IsFalse(Action(window, "Download").IsEnabled);
            Assert.IsTrue(Desc<TextBlock>(window).Any(text => text.Text.Contains(LetItOutModel.DisplayName, StringComparison.Ordinal)));
        }
        finally { window.Close(); owner.Close(); }
    });

    [TestMethod]
    public Task DownloadDoesNotEnterChatAndContinueRechecksReadiness() => Sta(async () =>
    {
        var owner = Owner(); var calls = new List<bool>(); var missing = true;
        var window = Create(owner, (download, _, _) =>
        {
            calls.Add(download); if (download) missing = false;
            return Task.FromResult(missing ? Missing() : Ready());
        });
        try
        {
            window.Show(); await Render(window);
            Assert.IsTrue(Action(window, "Download").IsEnabled);
            Assert.IsFalse(Action(window, "Continue").IsEnabled);
            await window.RunPreparationAsync(true);
            CollectionAssert.AreEqual(new[] { false, true }, calls);
            Assert.IsNull(window.CoreModelPath);
            Assert.IsTrue(window.IsVisible);
            Assert.IsTrue(Action(window, "Continue").IsEnabled);
            missing = true;
            await window.ContinueAsync();
            CollectionAssert.AreEqual(new[] { false, true, false }, calls);
            Assert.IsNull(window.CoreModelPath);
            Assert.IsFalse(Action(window, "Continue").IsEnabled);
            Assert.IsTrue(window.IsVisible);
        }
        finally { window.Close(); owner.Close(); }
    });

    [TestMethod]
    public Task ContinueReturnsVerifiedCorePathFromModalWindow() => Sta(async () =>
    {
        var owner = Owner(); var calls = new List<bool>();
        var window = Create(owner, (download, _, _) => { calls.Add(download); return Task.FromResult(Ready()); });
        var assertion = new TaskCompletionSource();
        _ = window.Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                await Render(window);
                Assert.IsNull(window.CoreModelPath);
                await window.ContinueAsync();
                assertion.TrySetResult();
            }
            catch (Exception error) { assertion.TrySetException(error); window.Close(); }
        });
        try
        {
            Assert.AreEqual(true, window.ShowDialog()); await assertion.Task;
            Assert.AreEqual("fixture-core.gguf", window.CoreModelPath);
            CollectionAssert.AreEqual(new[] { false, false }, calls);
        }
        finally { if (window.IsVisible) window.Close(); owner.Close(); }
    });

    [TestMethod]
    public Task ClosingRequestsCancellationAndWaitsForPreparationToUnwind() => Sta(async () =>
    {
        var owner = Owner(); var release = new TaskCompletionSource<LetItOutChatReadiness>();
        CancellationToken observed = default;
        var window = Create(owner, (_, _, token) => { observed = token; return release.Task; });
        try
        {
            window.Show(); await Render(window); window.Close();
            Assert.IsTrue(observed.IsCancellationRequested);
            Assert.IsTrue(window.IsVisible);
            Assert.IsFalse(Action(window, "Cancel").IsEnabled);
            release.SetResult(Ready()); await Render(window);
            Assert.IsFalse(window.IsVisible);
            Assert.IsNull(window.CoreModelPath);
        }
        finally { release.TrySetResult(Missing()); await Render(window); if (window.IsVisible) window.Close(); owner.Close(); }
    });

    [TestMethod]
    public Task FailureClearsReadinessAndDoesNotDisplayTechnicalException() => Sta(async () =>
    {
        var owner = Owner(); var fail = false;
        var window = Create(owner, (_, _, _) => fail
            ? Task.FromException<LetItOutChatReadiness>(new IOException("sensitive fixture implementation detail"))
            : Task.FromResult(Ready()));
        try
        {
            window.Show(); await Render(window); fail = true;
            await window.RunPreparationAsync(false);
            Assert.IsFalse(Action(window, "Continue").IsEnabled);
            Assert.IsNull(window.CoreModelPath);
            Assert.IsTrue(Action(window, "Verify").IsEnabled);
            Assert.IsTrue(Desc<TextBlock>(window).Any(text => text.Text == "LetItOut.Preparation.Failed"));
            Assert.IsFalse(Desc<TextBlock>(window).Any(text => text.Text.Contains("sensitive fixture", StringComparison.Ordinal)));
        }
        finally { window.Close(); owner.Close(); }
    });

    private static LetItOutChatReadiness Ready() => new("fixture-core.gguf", true, true, 1024, 0, "Ready", "CPU");
    private static LetItOutChatReadiness Missing() => new(null, false, true, 1024, 1024, "Missing", "CPU", CanDownload: true);
    private static LetItOutChatPreparationWindow Create(Window owner,
        Func<bool, IProgress<LetItOutChatPreparationProgress>, CancellationToken, Task<LetItOutChatReadiness>> prepare) =>
        new(owner, new StorageSettings(), 2, key => key, CancellationToken.None) { Prepare = prepare };
    private static Button Action(Window window, string suffix) => Desc<Button>(window)
        .Single(button => AutomationProperties.GetAutomationId(button) == "LetItOut.Preparation." + suffix);
    private static IEnumerable<T> Desc<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var nested in Desc<T>(child)) yield return nested;
        }
    }
    private static Window Owner()
    {
        var owner = new Window();
        foreach (var key in new[] { "WindowBackgroundBrush", "PanelBrush", "InputBrush", "SecondaryButtonBackgroundBrush" }) owner.Resources[key] = Brushes.Black;
        owner.Resources["TextPrimaryBrush"] = Brushes.White; owner.Resources["TextSecondaryBrush"] = Brushes.LightGray;
        owner.Resources["LineBrush"] = Brushes.Gray; owner.Resources["AccentBrush"] = Brushes.RoyalBlue; owner.Resources["UiBodyFontSize"] = 14d;
        owner.Show(); return owner;
    }
    private static async Task Render(Window window)
    { window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.Background); window.UpdateLayout(); }
    private static Task Sta(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); completion.TrySetResult(); }
                catch (Exception error) { completion.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
