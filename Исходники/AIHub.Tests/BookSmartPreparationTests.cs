using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Services.Books.Smart;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using Brushes = System.Windows.Media.Brushes;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class BookSmartPreparationTests
{
    [TestMethod]
    public Task ShowingAndChangingSelectionOnlyChecksAndExplicitPreparationUsesTheSelectedPair() => Sta(async () =>
    {
        var owner = CreateOwner();
        var calls = new List<(BookSmartSelection Selection, bool Download)>();
        var secondCheck = new TaskCompletionSource<BookSmartReadiness>();
        var window = new BookSmartPreparationWindow(owner, key => key, new(), (selection, download, _, _) =>
        {
            calls.Add((selection, download));
            if (calls.Count == 2) return secondCheck.Task;
            return Task.FromResult(Readiness(download));
        });
        try
        {
            window.Show(); await Render(window);
            Assert.AreEqual(1, calls.Count); Assert.IsFalse(calls[0].Download);
            Assert.IsFalse(Button(window, "Next").IsEnabled); Assert.IsTrue(Button(window, "Download").IsEnabled);
            Combo(window, "Ocr").SelectedIndex = 1;
            Assert.AreEqual(2, calls.Count); Assert.IsFalse(calls[1].Download);
            Assert.AreEqual("ovis", calls[1].Selection.OcrId);
            Assert.IsFalse(Button(window, "Next").IsEnabled);
            Assert.IsFalse(Combo(window, "Ocr").IsEnabled);
            Assert.IsFalse(Button(window, "Download").IsEnabled);
            secondCheck.SetResult(Readiness(false)); await Render(window);
            Combo(window, "Vision").SelectedIndex = 1; await Render(window);
            await window.PrepareAsync(true);
            Assert.IsTrue(calls.Last().Download);
            Assert.AreEqual(new BookSmartSelection("ovis", "beta"), calls.Last().Selection);
            Assert.IsTrue(Button(window, "Next").IsEnabled);
            Assert.IsFalse(window.Prepared, "Checking/preparing alone is not user approval to enter the mode.");
            var scroll = Desc<ScrollViewer>(window).Single();
            Assert.AreEqual(ScrollBarVisibility.Disabled, scroll.HorizontalScrollBarVisibility);
        }
        finally { secondCheck.TrySetResult(Readiness(false)); await Render(window); window.Close(); owner.Close(); }
    });

    [TestMethod]
    public Task ClosingDuringPreparationRequestsCancellationAndWaitsForTheCallback() => Sta(async () =>
    {
        var owner = CreateOwner();
        var release = new TaskCompletionSource<BookSmartReadiness>();
        CancellationToken observed = default;
        var window = new BookSmartPreparationWindow(owner, key => key, new(), (_, _, _, token) =>
        { observed = token; return release.Task; });
        try
        {
            window.Show(); await Render(window); window.Close();
            Assert.IsTrue(observed.IsCancellationRequested);
            Assert.IsTrue(window.IsVisible, "The window must wait for the actual preparation callback to finish.");
            Assert.IsFalse(Button(window, "Cancel").IsEnabled);
            release.SetResult(Readiness(true)); await Render(window);
            Assert.IsFalse(window.IsVisible); Assert.IsFalse(window.Prepared);
        }
        finally { release.TrySetResult(Readiness(false)); await Render(window); if (window.IsVisible) window.Close(); owner.Close(); }
    });

    [TestMethod]
    public Task FailedPreparationCannotLeaveTheNextButtonReady() => Sta(async () =>
    {
        var owner = CreateOwner(); var fail = false;
        var window = new BookSmartPreparationWindow(owner, key => key, new(), (_, _, _, _) =>
            fail ? Task.FromException<BookSmartReadiness>(new IOException("fixture verification failure")) : Task.FromResult(Readiness(true)));
        try
        {
            window.Show(); await Render(window);
            Assert.IsTrue(Button(window, "Next").IsEnabled);
            fail = true; await window.PrepareAsync(false);
            Assert.IsFalse(Button(window, "Next").IsEnabled); Assert.IsFalse(Button(window, "Download").IsEnabled);
            Assert.IsFalse(window.Prepared);
            Assert.IsTrue(Desc<TextBlock>(window).Any(value => value.Text.Contains("fixture verification failure", StringComparison.Ordinal)));
        }
        finally { window.Close(); owner.Close(); }
    });

    [TestMethod]
    public Task ReadySelectionRequiresExplicitNextAndReturnsTrueFromModalWindow() => Sta(async () =>
    {
        var owner = CreateOwner(); var downloads = 0;
        var window = new BookSmartPreparationWindow(owner, key => key, new(), (_, download, _, _) =>
        { if (download) downloads++; return Task.FromResult(Readiness(true)); });
        var failure = new TaskCompletionSource();
        _ = window.Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                await Render(window); Assert.IsTrue(Button(window, "Next").IsEnabled);
                Assert.IsFalse(window.Prepared);
                Button(window, "Next").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                failure.TrySetResult();
            }
            catch (Exception error) { failure.TrySetException(error); window.Close(); }
        });
        try
        {
            Assert.IsTrue(window.ShowDialog() == true); await failure.Task;
            Assert.IsTrue(window.Prepared); Assert.AreEqual(0, downloads);
        }
        finally { if (window.IsVisible) window.Close(); owner.Close(); }
    });

    private static BookSmartReadiness Readiness(bool ready) => new([new("Selected component", ready ? "ready" : "missing", ready ? 0 : 1024, ready)]);
    private static Button Button(Window window, string suffix) => Desc<Button>(window).Single(control => AutomationProperties.GetAutomationId(control) == "BookConverter.SmartPreparation." + suffix);
    private static ComboBox Combo(Window window, string suffix) => Desc<ComboBox>(window).Single(control => AutomationProperties.GetAutomationId(control) == "BookConverter.SmartPreparation." + suffix);
    private static IEnumerable<T> Desc<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index); if (child is T match) yield return match;
            foreach (var nested in Desc<T>(child)) yield return nested;
        }
    }
    private static Window CreateOwner()
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
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
