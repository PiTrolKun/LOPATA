using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Services.SongClip;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class SongClipPlaybackTests
{
    [TestMethod]
    public Task EmbeddedPauseResumesTheClockBeforeAndAfterFullscreenClose() => WithFixture(async fixture =>
    {
        await fixture.PlayEmbeddedAsync();
        await AssertPauseAndResumeAsync();

        var (window, media) = fixture.OpenFullscreen();
        await WaitForPlaybackAsync(media);
        window.Close();
        await UntilAsync(() => fixture.PlayerWindows.Length == 0, "Closing fullscreen must remove its owned player.");
        AssertReleased(media);
        AssertReleased(fixture.Preview);
        await fixture.PlayEmbeddedAsync();
        await AssertPauseAndResumeAsync();

        async Task AssertPauseAndResumeAsync()
        {
            var preview = fixture.Preview;
            await UntilAsync(() => preview.Position > TimeSpan.FromMilliseconds(650),
                "The embedded player must reach a nonzero position before testing Pause.");
            var source = preview.Source;
            Click(fixture.Control, "Play");
            Assert.IsTrue(preview.IsMuted, "The Pause branch must mute the existing decoder immediately.");
            await Task.Delay(180);
            var pausedPosition = preview.Position;
            await Task.Delay(350);
            Assert.AreEqual(source, preview.Source, "Pause must keep the current source available for resuming.");
            Assert.IsTrue(preview.IsMuted, "The embedded player must remain muted while paused.");
            Assert.IsTrue((preview.Position - pausedPosition).Duration() <= TimeSpan.FromMilliseconds(25),
                "The real media clock must stay still while paused, including after fullscreen has been closed.");

            Click(fixture.Control, "Play");
            await WaitForPlaybackAsync(preview);
            Assert.AreEqual(source, preview.Source, "Resume must keep playing the same result.");
            Assert.IsTrue(preview.Position > pausedPosition,
                "Explicit Resume must unmute and advance beyond the paused position, rather than restart the clip.");
        }
    });

    [TestMethod]
    public Task FullscreenCloseReleasesMediaAndDoesNotRestart() => WithFixture(async fixture =>
    {
        await fixture.PlayEmbeddedAsync();
        var (window, media) = fixture.OpenFullscreen();
        await WaitForPlaybackAsync(media);

        window.Close();
        await UntilAsync(() => fixture.PlayerWindows.Length == 0 && media.Source is null,
            "Closing fullscreen must remove its owned window and clear its media source.");
        AssertReleased(media);
        AssertReleased(fixture.Preview);
        var position = media.Position;
        var embeddedPosition = fixture.Preview.Position;

        // Cover the reported delayed restart, rather than inspecting only the synchronous close state.
        await Task.Delay(1250);
        AssertReleased(media);
        AssertReleased(fixture.Preview);
        Assert.AreEqual(position, media.Position, "A closed fullscreen decoder must not continue its playback clock.");
        Assert.AreEqual(embeddedPosition, fixture.Preview.Position, "Closing fullscreen must not resume the embedded player.");
        Assert.AreEqual(0, fixture.PlayerWindows.Length);
        await AssertFileReleasedAsync(fixture.SelectedPath);
    });

    [TestMethod]
    public Task RepeatedFullscreenClickKeepsOneOwnedPlayer() => WithFixture(async fixture =>
    {
        var (firstWindow, firstMedia) = fixture.OpenFullscreen();
        var (secondWindow, secondMedia) = fixture.OpenFullscreen();

        Assert.AreSame(firstWindow, secondWindow, "A second click must activate the existing player, not create another window.");
        Assert.AreSame(firstMedia, secondMedia, "A second click must not create another decoder for the same result.");
        Assert.AreEqual(1, fixture.PlayerWindows.Length);
        await WaitForPlaybackAsync(firstMedia);
        firstWindow.Close();
        await UntilAsync(() => fixture.PlayerWindows.Length == 0, "The single player must close normally.");
        AssertReleased(firstMedia);
        AssertReleased(fixture.Preview);
        await AssertFileReleasedAsync(fixture.SelectedPath);
    });

    [TestMethod]
    [DataRow("result")]
    [DataRow("project")]
    [DataRow("empty")]
    public Task ChangingOrClearingResultClosesPreviousPlayer(string change) => WithFixture(async fixture =>
    {
        await fixture.PlayEmbeddedAsync();
        var previousPath = fixture.SelectedPath;
        var (_, media) = fixture.OpenFullscreen();
        await WaitForPlaybackAsync(media);

        if (change == "result") fixture.Results.SelectedIndex = 1;
        else if (change == "empty") fixture.Results.SelectedIndex = -1;
        else fixture.Control.OpenProject(fixture.OtherProject.Id);

        await UntilAsync(() => fixture.PlayerWindows.Length == 0 && media.Source is null,
            "Changing or clearing the selected source must close the previous fullscreen player.");
        AssertReleased(media);
        AssertReleased(fixture.Preview);
        await AssertFileReleasedAsync(previousPath);

        Click(fixture.Control, "Play");
        if (change == "empty")
        {
            await Task.Delay(150);
            AssertReleased(fixture.Preview);
            Assert.AreEqual(0, fixture.PlayerWindows.Length, "An empty selection must not retain a playable stale URI.");
        }
        else
        {
            await WaitForPlaybackAsync(fixture.Preview);
            Assert.AreEqual(fixture.ExpectedPath(change), fixture.Preview.Source!.LocalPath,
                "Explicit Play must open the new result, rather than the decoder that belonged to the previous selection.");
        }
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task ClosingOwnerReleasesBothPlayers(bool fullscreen) => WithFixture(async fixture =>
    {
        await fixture.PlayEmbeddedAsync();
        MediaElement? expanded = null;
        if (fullscreen)
        {
            (_, expanded) = fixture.OpenFullscreen();
            await WaitForPlaybackAsync(expanded);
        }

        fixture.Owner.Close();
        await UntilAsync(() => !fixture.Owner.IsVisible && fixture.PlayerWindows.Length == 0,
            "Closing the owner must also close its fullscreen player.");
        AssertReleased(fixture.Preview);
        if (expanded is not null) AssertReleased(expanded);
        await AssertFileReleasedAsync(fixture.SelectedPath);
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task HiddenOrUnloadedWorkspaceStaysSilentUntilExplicitPlay(bool unload) => WithFixture(async fixture =>
    {
        await fixture.PlayEmbeddedAsync();
        var (_, media) = fixture.OpenFullscreen();
        await WaitForPlaybackAsync(media);

        fixture.LeaveWorkspace(unload);
        await UntilAsync(() => fixture.PlayerWindows.Length == 0 && media.Source is null,
            "Both hiding and unloading the workspace must close its external player.");
        AssertReleased(media);
        AssertReleased(fixture.Preview);
        await AssertFileReleasedAsync(fixture.SelectedPath);

        fixture.ReturnToWorkspace(unload);
        await UntilAsync(() => fixture.Control.IsLoaded && fixture.Control.IsVisible,
            "The existing workspace must return to the visual tree.");
        await Task.Delay(150);
        AssertReleased(fixture.Preview);
        Assert.AreEqual(0, fixture.PlayerWindows.Length, "Returning to the page must not reopen fullscreen or resume audio.");

        await fixture.PlayEmbeddedAsync();
        Assert.AreEqual(fixture.SelectedPath, fixture.Preview.Source!.LocalPath,
            "Explicit Play must restore the selected URI after navigation released the decoder.");
        fixture.LeaveWorkspace(unload);
        await UntilAsync(() => fixture.Preview.Source is null, "Leaving again must also release an embedded player.");
        AssertReleased(fixture.Preview);
        await AssertFileReleasedAsync(fixture.SelectedPath);
    });

    [TestMethod]
    public Task ImmediateFullscreenCloseCannotRestart() => WithFixture(async fixture =>
    {
        var (window, media) = fixture.OpenFullscreen();
        // Do not yield between opening and closing: native media may still be opening asynchronously.
        window.Close();
        await Task.Delay(1250);

        Assert.AreEqual(0, fixture.PlayerWindows.Length);
        AssertReleased(media);
        AssertReleased(fixture.Preview);
        await AssertFileReleasedAsync(fixture.SelectedPath);
        await fixture.PlayEmbeddedAsync();
        Assert.AreEqual(fixture.SelectedPath, fixture.Preview.Source!.LocalPath,
            "An interrupted fullscreen open must not poison subsequent explicit embedded playback.");
    });

    private static void AssertReleased(MediaElement media)
    {
        Assert.IsNull(media.Source, "Released playback must detach the URI so late native callbacks cannot reopen it.");
        Assert.IsTrue(media.IsMuted, "A retiring decoder must be muted before asynchronous native teardown.");
    }

    private static async Task WaitForPlaybackAsync(MediaElement media)
    {
        Exception? failed = null;
        EventHandler<ExceptionRoutedEventArgs> onFailed = (_, args) => failed = args.ErrorException;
        media.MediaFailed += onFailed;
        try
        {
            await UntilAsync(() => failed is not null ||
                (media.NaturalDuration.HasTimeSpan && media.Position > TimeSpan.FromMilliseconds(80)),
                "The real WPF decoder must open the own PCM WAV and advance its playback clock.");
            Assert.IsNull(failed, "WPF must accept the test WAV: " + failed);
            Assert.IsNotNull(media.Source);
            Assert.IsFalse(media.IsMuted, "Explicit Play must restore audible playback after earlier teardown muted the decoder.");
            var position = media.Position;
            await UntilAsync(() => failed is not null || media.Position > position + TimeSpan.FromMilliseconds(60),
                "Playback must keep advancing across two independent observations, rather than retain a stale position.");
            Assert.IsNull(failed, "WPF playback failed while its clock was advancing: " + failed);
        }
        finally { media.MediaFailed -= onFailed; }
    }

    private static async Task AssertFileReleasedAsync(string path) => await UntilAsync(() =>
    {
        try
        {
            using var file = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return file.Length > 44;
        }
        catch (IOException) { return false; }
    }, "The old media file must become exclusively accessible after its WPF decoders close.");

    private static async Task UntilAsync(Func<bool> ready, string message)
    {
        var elapsed = Stopwatch.StartNew();
        while (!ready())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(10)) throw new AssertFailedException(message);
            await Task.Delay(20);
        }
    }

    private static void Click(DependencyObject root, string key) => Find<Button>(root, "SongClip." + key)
        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject => Descendants(root)
        .OfType<T>().Single(node => AutomationProperties.GetAutomationId(node) == id);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var node in Descendants(child)) yield return node;
    }

    private static Task WithFixture(Func<Fixture, Task> action) => OnUi(async () =>
    {
        var fixture = new Fixture();
        var errors = new List<Exception>();
        try { await fixture.ShowAsync(); await action(fixture); }
        catch (Exception error) { errors.Add(error); }
        errors.AddRange(await fixture.CleanupAsync());
        if (errors.Count > 0) throw new AggregateException("Playback regression or cleanup failed.", errors);
    });

    private sealed class Fixture
    {
        private readonly VideoPreparationTests.TestFiles _files = new();
        private bool _ownerClosed;
        public SongClipControl Control { get; }
        public Window Owner { get; }
        public SongClipProject OtherProject { get; }
        public MediaElement Preview => Find<MediaElement>(Control, "SongClip.Preview");
        public ListBox Results => Find<ListBox>(Control, "SongClip.Results");
        public string FirstPath { get; }
        public string SecondPath { get; }
        public string SelectedPath => FirstPath;
        public Window[] PlayerWindows => Owner.OwnedWindows.Cast<Window>()
            .Where(window => AutomationProperties.GetAutomationId(window) == "SongClip.Player.Window").ToArray();

        public Fixture()
        {
            var store = new SongClipStore(_files.File("projects"));
            var project = new SongClipProject { Name = "Own playback fixture" };
            FirstPath = store.Resolve(project.Id, "results/first.wav");
            SecondPath = store.Resolve(project.Id, "results/second.wav");
            WriteSilentWave(FirstPath); WriteSilentWave(SecondPath);
            project.Results.Add(Result("results/first.wav"));
            project.Results.Add(Result("results/second.wav"));
            store.Save(project);
            OtherProject = new SongClipProject { Name = "Another own playback fixture" };
            var otherPath = store.Resolve(OtherProject.Id, "results/second.wav");
            Directory.CreateDirectory(Path.GetDirectoryName(otherPath)!);
            File.Copy(SecondPath, otherPath);
            OtherProject.Results.Add(Result("results/second.wav"));
            store.Save(OtherProject);
            // Both projects use their own owned file. This is the expected path after a project switch.
            _otherPath = otherPath;
            Control = new SongClipControl(key => key, store);
            Control.OpenProject(project.Id);
            Owner = new Window
            {
                Content = Control, Width = 1400, Height = 850, ShowActivated = false, ShowInTaskbar = false,
                WindowStyle = WindowStyle.ToolWindow
            };
            Owner.Closed += (_, _) => _ownerClosed = true;
            Owner.Resources["PanelBrush"] = Brushes.DarkSlateGray;
            Owner.Resources["SecondaryButtonBackgroundBrush"] = Brushes.DarkSlateGray;
            Owner.Resources["InputBrush"] = Brushes.Black;
            Owner.Resources["WindowBackgroundBrush"] = Brushes.Black;
            Owner.Resources["TextPrimaryBrush"] = Brushes.White;
            Owner.Resources["TextSecondaryBrush"] = Brushes.LightGray;
            Owner.Resources["LineBrush"] = Brushes.DimGray;
            Owner.Resources["AccentBrush"] = Brushes.RoyalBlue;
            Owner.Resources["UiBodyFontSize"] = 14d;
        }

        private readonly string _otherPath;
        public string ExpectedPath(string change) => change == "project" ? _otherPath : SecondPath;

        public async Task ShowAsync()
        {
            Owner.Show(); Owner.UpdateLayout();
            await UntilAsync(() => Control.IsLoaded && Control.IsVisible, "The actual WPF workspace must load.");
            Results.SelectedIndex = 0;
        }

        public async Task PlayEmbeddedAsync()
        {
            Click(Control, "Play");
            await WaitForPlaybackAsync(Preview);
        }

        public (Window Window, MediaElement Media) OpenFullscreen()
        {
            Click(Control, "Fullscreen");
            var window = PlayerWindows.Single();
            return (window, Find<MediaElement>(window, "SongClip.Player.Media"));
        }

        public void LeaveWorkspace(bool unload)
        {
            if (unload) Owner.Content = null;
            else Control.Visibility = Visibility.Collapsed;
        }

        public void ReturnToWorkspace(bool unload)
        {
            if (unload) Owner.Content = Control;
            else Control.Visibility = Visibility.Visible;
            Owner.UpdateLayout();
        }

        public async Task<IReadOnlyList<Exception>> CleanupAsync()
        {
            var errors = new List<Exception>();
            void Attempt(Action cleanup)
            {
                try { cleanup(); }
                catch (Exception error) { errors.Add(error); }
            }
            // Keep a regression failure from leaving native playback/windows behind in the test process.
            foreach (var window in Owner.OwnedWindows.Cast<Window>().ToArray())
            {
                foreach (var media in Descendants(window).OfType<MediaElement>().ToArray()) Attempt(() => ForceRelease(media));
                Attempt(window.Close);
            }
            foreach (var media in Descendants(Control).OfType<MediaElement>().ToArray()) Attempt(() => ForceRelease(media));
            if (!_ownerClosed) Attempt(Owner.Close);
            foreach (var path in new[] { FirstPath, SecondPath, _otherPath })
            {
                try { await AssertFileReleasedAsync(path); }
                catch (Exception error) { errors.Add(error); }
            }
            Attempt(_files.Dispose);
            return errors;
        }

        private static void ForceRelease(MediaElement media)
        {
            media.LoadedBehavior = MediaState.Manual;
            media.UnloadedBehavior = MediaState.Close;
            media.IsMuted = true;
            media.Close(); media.Source = null;
        }

        private static SongClipResult Result(string file) => new(Guid.NewGuid().ToString("N"), file,
            "glitchframe", 6, DateTimeOffset.UtcNow);

        private static void WriteSilentWave(string path)
        {
            const int sampleRate = 8000, bytesPerSample = 2, seconds = 6;
            var bytes = sampleRate * bytesPerSample * seconds;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var output = new BinaryWriter(File.Create(path), Encoding.ASCII);
            output.Write(Encoding.ASCII.GetBytes("RIFF")); output.Write(36 + bytes);
            output.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); output.Write(16);
            output.Write((short)1); output.Write((short)1); output.Write(sampleRate);
            output.Write(sampleRate * bytesPerSample); output.Write((short)bytesPerSample); output.Write((short)16);
            output.Write(Encoding.ASCII.GetBytes("data")); output.Write(bytes); output.Write(new byte[bytes]);
        }
    }

    private static Task OnUi(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var unhandled = new List<Exception>();
            DispatcherUnhandledExceptionEventHandler capture = (_, args) =>
            {
                unhandled.Add(args.Exception);
                args.Handled = true;
            };
            dispatcher.UnhandledException += capture;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                Exception? failure = null;
                try { await action(); }
                catch (Exception error) { failure = error; }
                finally
                {
                    // Native media callbacks can arrive after the last synchronous Close call.
                    await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                    if (unhandled.Count > 0)
                        failure = new AggregateException("Playback raised unhandled WPF Dispatcher exceptions.",
                            failure is null ? unhandled : unhandled.Prepend(failure));
                    if (failure is null) done.TrySetResult(); else done.TrySetException(failure);
                    dispatcher.UnhandledException -= capture;
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return done.Task.WaitAsync(TimeSpan.FromSeconds(55));
    }
}
