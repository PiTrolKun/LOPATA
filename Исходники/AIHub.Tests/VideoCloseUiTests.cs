using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Controls.VideoEditor;
using AIHub.Services;
using AIHub.Services.VideoEditor;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoCloseUiTests
{
    private static string Root => Environment.GetEnvironmentVariable("LOPATA_VIDEO_CLOSE_EVIDENCE") ?? Path.Combine(Path.GetTempPath(), "lopata-video-close");
    [TestMethod, DataRow(0), DataRow(1)]
    public async Task FreshProjectWithZeroOrOneEditClosesImmediately(int edits) => await WithEditor(false, async (editor, _) => {
        editor.ConfirmSaveOnClose = () => throw new AssertFailedException("A new project below the threshold must not ask");
        for (var i = 0; i < edits; i++) Click(editor, "AddVideo");
        editor.Close(); Assert.IsFalse(editor.IsVisible); await Task.CompletedTask;
    });
    [TestMethod]
    public async Task ActualPromptCanCancelThenSaveNewPortableProject() => await WithEditor(false, async (editor, folder) => {
        Click(editor, "AddVideo"); Click(editor, "AddAudio");
        var cancelled = PressPrompt(editor, "CancelClose", dialog => {
            Assert.IsTrue(dialog.Title.Contains("Сохранить", StringComparison.Ordinal));
            editor.Close(); Assert.AreEqual(1, editor.OwnedWindows.Cast<Window>().Count(), "Repeated close must keep the same prompt");
            dialog.UpdateLayout(); var shot = new RenderTargetBitmap((int)Math.Ceiling(dialog.ActualWidth), (int)Math.Ceiling(dialog.ActualHeight), 96, 96, PixelFormats.Pbgra32); shot.Render(dialog);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(shot)); using var file = File.Create(Path.Combine(folder, "close-prompt.png")); encoder.Save(file);
        });
        editor.Close(); await cancelled; await Until(() => !editor.ClosePending); Assert.IsTrue(editor.IsVisible);
        var archive = Path.Combine(folder, "Сохранённый проект.lopataVideo"); editor.SelectArchiveDestination = () => archive;
        var saved = PressPrompt(editor, "Save"); editor.Close(); await saved; await Until(() => !editor.IsVisible);
        var p = VideoProjectStore.Load(archive); Assert.AreEqual(4, p.Tracks.Count); Assert.AreEqual("Сохранённый проект", p.Name);
        Assert.AreEqual("LOPATA", p.Provenance.Program);
    });
    [TestMethod]
    public async Task CancelledPickerAndProtectedDestinationKeepEditorOpenAndDiscardPreservesOriginal() => await WithEditor(false, async (editor, folder) => {
        Click(editor, "AddVideo"); Click(editor, "AddAudio"); var requests = 0;
        editor.ConfirmSaveOnClose = () => { requests++; return VideoCloseChoice.Save; }; editor.SelectArchiveDestination = () => null;
        editor.Close(); await Until(() => !editor.ClosePending); Assert.IsTrue(editor.IsVisible); Assert.AreEqual(1, requests);
        var protectedPath = Path.Combine(folder, "other.lopataVideo"); VideoProjectArchive.Save(new(), protectedPath); var before = SHA256.HashData(await File.ReadAllBytesAsync(protectedPath));
        editor.SelectArchiveDestination = () => protectedPath; editor.Close(); await Until(() => !editor.ClosePending);
        Assert.IsTrue(editor.IsVisible); Assert.AreEqual(2, requests); CollectionAssert.AreEqual(before, SHA256.HashData(await File.ReadAllBytesAsync(protectedPath)));
        var discarded = PressPrompt(editor, "CloseWithoutSaving"); editor.ConfirmSaveOnClose = null; editor.Close(); await discarded; await Until(() => !editor.IsVisible);
        Assert.AreEqual(4, VideoProjectStore.Load(VideoProjectStore.Recovery(editor.ProjectId)).Tracks.Count, "Recovery is still available after explicitly discarding archive save");
    });
    [TestMethod]
    public async Task PreviouslySavedProjectAsksAfterOneEditAndWritesBackToSameArchive() => await WithEditor(true, async (editor, folder) => {
        Click(editor, "AddVideo"); var requests = 0; editor.ConfirmSaveOnClose = () => { requests++; return VideoCloseChoice.Cancel; };
        editor.Close(); Assert.IsTrue(editor.IsVisible); Assert.AreEqual(1, requests);
        Click(editor, "Undo"); await Task.Delay(100); Assert.IsFalse(editor.Title.EndsWith('*'));
        editor.Close(); Assert.IsFalse(editor.IsVisible); Assert.AreEqual(1, requests, "Undo back to the saved state must close without another request");
        Assert.AreEqual(2, VideoProjectStore.Load(Path.Combine(folder, "initial.lopataVideo")).Tracks.Count);
    });
    [TestMethod]
    public async Task SavedProjectClosesOnlyAfterSuccessfulSaveWithoutAnotherPathPicker() => await WithEditor(true, async (editor, folder) => {
        Click(editor, "AddVideo"); editor.ConfirmSaveOnClose = () => VideoCloseChoice.Save;
        editor.SelectArchiveDestination = () => throw new AssertFailedException("An existing archive must keep its path");
        editor.Close(); await Until(() => !editor.IsVisible);
        var saved = VideoProjectStore.Load(Path.Combine(folder, "initial.lopataVideo")); Assert.AreEqual(3, saved.Tracks.Count);
    });
    [TestMethod]
    public async Task ClosingDuringArchiveWriteCancelsSaveAndKeepsWindowOpen() => await WithEditor(false, async (editor, folder) => {
        var source = Path.Combine(folder, "attachment.bin"); using (var file = File.Create(source)) file.SetLength(64 * 1024 * 1024);
        var info = new FileInfo(source); var timeline = Descendants(editor).OfType<VideoTimeline>().Single();
        timeline.Project.ImportedFiles.Add(new(info.FullName, info.Name, info.Length, info.LastWriteTimeUtc));
        Click(editor, "AddVideo"); Click(editor, "AddAudio"); editor.ConfirmSaveOnClose = () => VideoCloseChoice.Save;
        var destination = Path.Combine(folder, "cancelled.lopataVideo"); var interrupted = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1) };
        timer.Tick += (_, _) => {
            if (typeof(VideoEditorWindow).GetField("_savingArchive", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(editor) is true) { timer.Stop(); interrupted = true; editor.Close(); }
        };
        editor.SelectArchiveDestination = () => { timer.Start(); return destination; };
        try { editor.Close(); await Until(() => !editor.ClosePending); }
        finally { timer.Stop(); }
        Assert.IsTrue(interrupted, "The test must interrupt a real active archive write"); Assert.IsTrue(editor.IsVisible); Assert.IsFalse(File.Exists(destination)); Assert.AreEqual(64 * 1024 * 1024, new FileInfo(source).Length);
        editor.ConfirmSaveOnClose = () => VideoCloseChoice.Discard; editor.Close(); Assert.IsFalse(editor.IsVisible);
    });
    [TestMethod]
    public async Task ManualSaveThenImmediateCloseDoesNotAskAgain() => await WithEditor(false, async (editor, folder) => {
        Click(editor, "AddVideo"); Click(editor, "AddAudio");
        var path = Path.Combine(folder, "manual.lopataVideo"); editor.SelectArchiveDestination = () => path;
        Click(editor, "Save"); await Until(() => File.Exists(path) && !editor.Title.EndsWith('*'));
        editor.ConfirmSaveOnClose = () => throw new AssertFailedException("A successfully saved project must close directly");
        editor.Close(); Assert.IsFalse(editor.IsVisible); Assert.AreEqual(4, VideoProjectStore.Load(path).Tracks.Count);
    });
    private static async Task WithEditor(bool saved, Func<VideoEditorWindow, string, Task> test)
    {
        var folder = Path.Combine(Root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        await OnUi(async () => {
            using var policy = ComponentAudit.UseTestStore(Path.Combine(folder, "audit"), false); var gate = ComponentLicenseGate.ConfirmAsync; ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
            var owner = new Window { Width = 100, Height = 100, ShowActivated = false };
            owner.Resources["WindowBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(14, 22, 38)); owner.Resources["TextPrimaryBrush"] = Brushes.WhiteSmoke; owner.Resources["LineBrush"] = Brushes.SlateGray;
            owner.Resources["PanelBrush"] = new SolidColorBrush(Color.FromRgb(23, 32, 49)); owner.Resources["SecondaryButtonBackgroundBrush"] = Brushes.DarkSlateGray; owner.Resources["AccentBrush"] = Brushes.RoyalBlue; owner.Resources["UiBodyFontSize"] = 14d;
            VideoEditorWindow? editor = null;
            try {
                var p = new VideoProject(); var path = saved ? Path.Combine(folder, "initial.lopataVideo") : null; if (path is not null) VideoProjectArchive.Save(p, path);
                var l = new LocalizationService(); l.Load("ru"); owner.Show(); editor = new(owner, l.T, "ru", new VideoWork(), p, path) { ShowActivated = false }; editor.Show(); await editor.Ready.Task.WaitAsync(TimeSpan.FromSeconds(45));
                await test(editor, folder);
            }
            finally { if (editor?.IsVisible == true) { editor.ConfirmSaveOnClose = () => VideoCloseChoice.Discard; editor.Close(); } owner.Close(); ComponentLicenseGate.ConfirmAsync = gate; }
        });
    }
    private static Task PressPrompt(VideoEditorWindow editor, string key, Action<Window>? inspect = null)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var deadline = DateTime.UtcNow.AddSeconds(8); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
        timer.Tick += (_, _) => {
            var dialog = editor.OwnedWindows.Cast<Window>().FirstOrDefault(w => AutomationProperties.GetAutomationId(w) == "VideoEditor.ClosePrompt");
            if (dialog is null) { if (DateTime.UtcNow > deadline) { timer.Stop(); done.TrySetException(new TimeoutException("Close prompt did not appear")); } return; }
            timer.Stop(); try { inspect?.Invoke(dialog); Click(dialog, "Close." + key); done.TrySetResult(); } catch (Exception e) { dialog.Close(); done.TrySetException(e); }
        };
        timer.Start(); return done.Task;
    }
    private static void Click(DependencyObject root, string key) => Descendants(root).OfType<Button>().First(b => AutomationProperties.GetAutomationId(b) == "VideoEditor." + key).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root) { yield return root; foreach (var c in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) foreach (var item in Descendants(c)) yield return item; }
    private static async Task Until(Func<bool> ready) { var end = DateTime.UtcNow.AddSeconds(12); while (!ready()) { if (DateTime.UtcNow > end) throw new TimeoutException("Close operation did not settle"); await Task.Delay(25); } }
    private static Task OnUi(Func<Task> action) {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { var d = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(d)); d.BeginInvoke(async () => { try { await action(); done.TrySetResult(); } catch (Exception e) { done.TrySetException(e); } finally { d.BeginInvokeShutdown(DispatcherPriority.Background); } }); Dispatcher.Run(); }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task;
    }
}
