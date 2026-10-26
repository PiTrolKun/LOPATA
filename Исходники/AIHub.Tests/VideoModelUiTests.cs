using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Controls.VideoEditor;
using AIHub.Services;
using AIHub.Services.VideoEditor;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoModelUiTests
{
    [TestMethod, DataRow("music"), DataRow("analyze"), DataRow("speech")]
    public async Task PreparationWindowClosesAndContinuesOnlyWhenReady(string action)
    {
        await OnUi(async () => {
            var l = new LocalizationService(); l.Load("ru"); var owner = Owner(); owner.Show();
            try {
                var calls = new List<bool>(); var started = 0;
                var dialog = new VideoPreparationWindow(owner, l.T, action, (download, progress, token) => { token.ThrowIfCancellationRequested(); calls.Add(download); return Task.FromResult(download); }, default);
                var driver = Dispatcher.CurrentDispatcher.InvokeAsync(async () => { await Task.Delay(80); await dialog.ExecuteAsync(true); });
                if (dialog.ShowDialog() == true) started++; await driver.Task.Unwrap();
                Assert.AreEqual(1, started); CollectionAssert.AreEqual(new[] { false, true }, calls);
                var cancelled = new VideoPreparationWindow(owner, l.T, action, (_, _, _) => Task.FromResult(false), default);
                _ = Dispatcher.CurrentDispatcher.BeginInvoke(() => cancelled.Close(), DispatcherPriority.Background);
                if (cancelled.ShowDialog() == true) started++; Assert.AreEqual(1, started, "Cancelling preparation must not start a model operation");
                var ready = new VideoPreparationWindow(owner, l.T, action, (_, _, _) => Task.FromResult(true), default);
                Assert.IsTrue(ready.ShowDialog() == true, "A successful verification closes the standard window automatically");
            }
            finally { owner.Close(); }
        });
    }
    [TestMethod]
    public async Task EditorButtonCapturesLengthBeforePreparationAndCancelledPreparationDoesNothing()
    {
        using var f = new VideoMusicTests.Folder();
        await OnUi(async () => {
            using var policy = ComponentAudit.UseTestStore(Path.Combine(f.Path, "audit"), false); var gate = ComponentLicenseGate.ConfirmAsync; ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
            var owner = Owner(); var l = new LocalizationService(); l.Load("ru"); VideoEditorWindow? editor = null;
            try {
                var p = new VideoProject { Name = "Фоновая музыка UI", Width = 320, Height = 180 }; p.Tracks[0].Clips.Add(new() { Text = "Проверка", Duration = 5 });
                var worker = new VideoMusicTests.Worker(); owner.Show(); editor = new(owner, l.T, "ru", new VideoWork(musicWorker: worker), p) { ConfirmSaveOnClose = () => VideoCloseChoice.Discard, ShowActivated = false }; editor.Show(); await editor.Ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                editor.PrepareModels = async (need, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); return new(f.Path); };
                var button = Descendants(editor).OfType<Button>().Single(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "VideoEditor.CreateBackgroundMusic");
                button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                p.Tracks[0].Clips[0].Duration = 9; release.TrySetResult(); await WaitIdle(editor);
                Assert.AreEqual(1, worker.Calls); Assert.AreEqual(5, worker.Request!.DurationSeconds); Assert.AreEqual(5, p.Tracks.Single(t => t.BackgroundMusic).Clips.Single().Duration, .03);
                var before = JsonSerializer.Serialize(p); editor.PrepareModels = (_, _) => Task.FromResult<VideoPreparedModels?>(null);
                button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); await WaitIdle(editor);
                Assert.AreEqual(1, worker.Calls); Assert.AreEqual(before, JsonSerializer.Serialize(p));
            }
            finally { editor?.Close(); owner.Close(); ComponentLicenseGate.ConfirmAsync = gate; }
        });
    }
    private static async Task WaitIdle(VideoEditorWindow editor)
    {
        var busy = typeof(VideoEditorWindow).GetField("_busy", BindingFlags.Instance | BindingFlags.NonPublic)!; var watch = System.Diagnostics.Stopwatch.StartNew();
        while ((bool)busy.GetValue(editor)! && watch.Elapsed.TotalSeconds < 30) await Task.Delay(30);
        Assert.IsFalse((bool)busy.GetValue(editor)!); await Task.Delay(60);
    }
    private static Window Owner()
    {
        var owner = new Window { Width = 100, Height = 100, ShowActivated = false };
        owner.Resources["WindowBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(14, 22, 38)); owner.Resources["TextPrimaryBrush"] = Brushes.WhiteSmoke; owner.Resources["LineBrush"] = Brushes.SlateGray;
        owner.Resources["PanelBrush"] = new SolidColorBrush(Color.FromRgb(23, 32, 49)); owner.Resources["SecondaryButtonBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(16, 24, 40)); owner.Resources["AccentBrush"] = Brushes.RoyalBlue; owner.Resources["UiBodyFontSize"] = 14d; return owner;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    { yield return root; foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) foreach (var item in Descendants(child)) yield return item; }
    private static Task OnUi(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher)); dispatcher.BeginInvoke(async () => { try { await action(); done.TrySetResult(); } catch (Exception error) { done.TrySetException(error); } finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } }); Dispatcher.Run(); }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task;
    }
}
