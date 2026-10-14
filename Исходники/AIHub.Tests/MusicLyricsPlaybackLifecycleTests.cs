using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicLyricsPlaybackLifecycleTests
{
    [TestMethod, DataRow("ru", 220), DataRow("en", 220), DataRow("ru", 350), DataRow("en", 350)]
    public Task SynchronizationMessageFitsThePlayerCardInBothLanguages(string language, int width) => Sta(() => {
        using var f = new Fixture(); using var player = new MusicPlayerControl(new FakeAudio());
        var texts = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Localization", language + ".json")))!;
        player.Resources["TextPrimaryBrush"] = System.Windows.Media.Brushes.White;
        player.Resources["AccentBrush"] = System.Windows.Media.Brushes.RoyalBlue;
        player.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(23, 32, 50));
        player.Localize(key => texts.GetValueOrDefault(key, key)); player.Select(f.A);
        player.SetLyricsPreparation(f.A, pending: true);
        ScenarioNavigationTests.LogicalDescendants(player).OfType<Button>()
            .Single(button => AutomationProperties.GetAutomationId(button) == "Music.Audio.Play")
            .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        player.Measure(new Size(width, 180)); player.Arrange(new Rect(0, 0, width, 180)); player.UpdateLayout();
        var status = ScenarioNavigationTests.LogicalDescendants(player).OfType<TextBlock>()
            .Single(text => AutomationProperties.GetAutomationId(text) == "Music.Audio.Readiness");
        Assert.AreEqual(texts["Music.Audio.SyncWaitingPlay"], status.Text);
        var bounds = status.TransformToAncestor(player).TransformBounds(new Rect(status.RenderSize));
        Assert.IsTrue(bounds.Bottom <= 180, $"The message was clipped at {width}px: {bounds}.");
        Assert.IsTrue(bounds.Width <= width && bounds.Height > 0);
        var fullMessage = new TextBlock { Text = status.Text, FontFamily = status.FontFamily,
            FontSize = status.FontSize, TextWrapping = TextWrapping.Wrap };
        fullMessage.Measure(new Size(status.ActualWidth, double.PositiveInfinity));
        Assert.IsTrue(fullMessage.DesiredSize.Height <= status.ActualHeight + .5,
            $"The card hid part of the message: needed={fullMessage.DesiredSize.Height}, actual={status.ActualHeight}.");
        if (Environment.GetEnvironmentVariable("AIHUB_WAIT_PLAY_SCREENSHOTS") is { Length: > 0 } folder) {
            Directory.CreateDirectory(folder);
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, 180, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(player); var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(folder, $"player-{language}-{width}.png")); encoder.Save(output);
        }
    });

    [TestMethod]
    public Task PlayDuringTheInitialTimingReadWaitsThenStartsOnlyOnce() => Sta(() => {
        using var f = new Fixture();
        var completion = new TaskCompletionSource<MusicTimedLyrics?>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Sync.Read = _ => completion.Task;
        f.View.Player.Localize(key => key); f.View.LyricsPlayback.Configure(key => key);
        Select(f, f.A); Play(f.View); Seek(f.View, 1);
        Assert.IsFalse(f.Audio.IsPlaying); Assert.AreEqual("Music.Audio.SyncWaitingPlay", PlayerStatus(f.View));
        completion.SetResult(f.Timing(f.JobA)); Pump(() => f.Audio.IsPlaying);
        Assert.AreEqual(1, f.Audio.ToggleCount); Assert.AreEqual(1, f.Audio.Position.TotalSeconds);
        Assert.AreEqual("", PlayerStatus(f.View)); AssertHighlight(f.View, 0, Fixture.First.Length);
        Drain(); Assert.AreEqual(1, f.Audio.ToggleCount);
    });

    [TestMethod, DataRow(false), DataRow(true)]
    public Task PreparingAnOldSongAutoStartsOnlyWhenPlayWasRequested(bool requested) => Sta(() => {
        using var f = new Fixture(); var ready = false;
        var completion = new TaskCompletionSource<MusicTimedLyrics?>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Sync.Read = call => Task.FromResult<MusicTimedLyrics?>(ready ? f.Timing(call.Job) : null);
        f.Sync.Analyze = (_, _, _) => completion.Task;
        f.View.Player.Localize(key => key); f.View.LyricsPlayback.Configure(key => key);
        Select(f, f.A); Pump(() => f.Sync.Analyses == 1);
        Assert.AreEqual("Music.Audio.SyncPending", PlayerStatus(f.View));
        if (requested) Play(f.View);
        Assert.IsFalse(f.Audio.IsPlaying);
        ready = true; completion.SetResult(f.Timing(f.JobA)); Pump(() => Timing(f.View) is not null);
        Assert.AreEqual(requested, f.Audio.IsPlaying); Assert.AreEqual(requested ? 1 : 0, f.Audio.ToggleCount);
        Assert.AreEqual(1, f.Sync.Analyses);
    });

    [TestMethod]
    public Task SwitchingAwayCancelsQueuedPlayEvenWhenReturningToTheSameSong() => Sta(() => {
        using var f = new Fixture(); var readyA = false;
        var completion = new TaskCompletionSource<MusicTimedLyrics?>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Sync.Read = call => Task.FromResult<MusicTimedLyrics?>(call.Job.Id != f.JobA.Id || readyA ? f.Timing(call.Job) : null);
        f.Sync.Analyze = (_, _, _) => completion.Task;
        f.View.Player.Localize(key => key); f.View.LyricsPlayback.Configure(key => key);
        Select(f, f.A); Pump(() => f.Sync.Analyses == 1); Play(f.View);
        Select(f, f.B); Pump(() => Timing(f.View)?.JobId == f.B.JobId);
        Assert.IsFalse(f.Audio.IsPlaying); Assert.AreEqual(0, f.Audio.ToggleCount);
        Select(f, f.A); readyA = true; completion.SetResult(f.Timing(f.JobA));
        Pump(() => Timing(f.View)?.JobId == f.A.JobId);
        Assert.IsFalse(f.Audio.IsPlaying); Assert.AreEqual(0, f.Audio.ToggleCount);
    });

    [TestMethod, DataRow("click"), DataRow("pause"), DataRow("clear"), DataRow("unload")]
    public Task UserCanCancelQueuedPlaybackBeforeSynchronizationFinishes(string action) => Sta(() => {
        using var f = new Fixture();
        var completion = new TaskCompletionSource<MusicTimedLyrics?>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Sync.Read = _ => completion.Task;
        f.View.Player.Localize(key => key); f.View.LyricsPlayback.Configure(key => key);
        Select(f, f.A); Play(f.View);
        switch (action) {
            case "click": Play(f.View); break;
            case "pause": f.View.Player.Pause(); break;
            case "clear": f.View.Player.Clear(); break;
            case "unload": f.View.Player.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); break;
        }
        completion.SetResult(f.Timing(f.JobA)); Drain();
        Assert.IsFalse(f.Audio.IsPlaying); Assert.AreEqual(0, f.Audio.ToggleCount);
        Assert.AreNotEqual("Music.Audio.SyncWaitingPlay", PlayerStatus(f.View));
    });

    [TestMethod]
    public Task RefusedSynchronizationStartsRequestedNormalPlaybackWithAVisibleWarning() => Sta(() => {
        using var f = new Fixture();
        var completion = new TaskCompletionSource<MusicTimedLyrics?>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Sync.Analyze = (_, _, _) => completion.Task;
        f.View.Player.Localize(key => key); f.View.LyricsPlayback.Configure(key => key);
        Select(f, f.A); Pump(() => f.Sync.Analyses == 1); Play(f.View);
        completion.SetException(new IOException("ASR failed")); Pump(() => f.Audio.IsPlaying);
        Assert.AreEqual("Music.Audio.KaraokeUnavailable", PlayerStatus(f.View));
        Assert.AreEqual(1, f.Audio.ToggleCount); Assert.IsNull(Timing(f.View));
    });

    [TestMethod]
    public Task PlayerSelectionAndClearOwnTheBindingWithoutDependingOnTheTrackList() => Sta(() => {
        using var f = new Fixture(); f.Sync.Read = call => Task.FromResult<MusicTimedLyrics?>(f.Timing(call.Job));
        f.View.Player.Select(f.A); Pump(() => Timing(f.View)?.JobId == f.A.JobId);
        Seek(f.View, 1); AssertHighlight(f.View, 0, Fixture.First.Length);
        f.View.Player.Clear(); Assert.IsNull(Timing(f.View)); AssertHighlight(f.View, 0, 0);
        f.View.Player.Select(f.B); Pump(() => Timing(f.View)?.JobId == f.B.JobId);
        Seek(f.View, 1); AssertHighlight(f.View, Fixture.First.Length + 1, Fixture.Second.Length);
    });

    [TestMethod]
    public Task RapidAToBToARejectsCanceledReadsEvenWhenTheSameTrackReturns() => Sta(() => {
        using var f = new Fixture(); var completions = new ConcurrentDictionary<int, TaskCompletionSource<MusicTimedLyrics?>>();
        f.Sync.Read = call => {
            var completion = new TaskCompletionSource<MusicTimedLyrics?>(TaskCreationOptions.RunContinuationsAsynchronously);
            completions[call.Number] = completion; return completion.Task; // Deliberately ignores cancellation.
        };
        Select(f, f.A); Pump(() => completions.ContainsKey(1));
        Select(f, f.B); Pump(() => completions.ContainsKey(2));
        Select(f, f.A); Pump(() => completions.ContainsKey(3));
        completions[3].SetResult(f.Timing(f.JobA)); Pump(() => Timing(f.View)?.JobId == f.A.JobId);
        var accepted = Timing(f.View); Seek(f.View, 1); AssertHighlight(f.View, 0, Fixture.First.Length);
        completions[2].SetResult(f.Timing(f.JobB)); completions[1].SetException(new IOException("Late obsolete A read"));
        Drain(); Assert.AreSame(accepted, Timing(f.View)); AssertHighlight(f.View, 0, Fixture.First.Length);
        Assert.IsFalse(Log(f.View).Contains("Late obsolete A read", StringComparison.Ordinal));
        Assert.AreEqual(Fixture.Lyrics, f.View.Editor.Lyrics);
    });

    [TestMethod]
    public Task ClearingThePlayerCancelsAnUnfinishedTimingRead() => Sta(() => {
        using var f = new Fixture(); var completion = new TaskCompletionSource<MusicTimedLyrics?>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Sync.Read = _ => completion.Task;
        Select(f, f.A); Pump(() => f.Sync.Reads.Count == 1); f.View.Player.Clear();
        completion.SetResult(f.Timing(f.JobA)); Drain();
        Assert.IsNull(Timing(f.View)); Assert.IsNull(f.View.Player.CurrentTrack); AssertHighlight(f.View, 0, 0);
    });

    [TestMethod]
    public Task ActualMediaPlayerRestoresTheCorrectClockAcrossAToBToAAndPausePlay() => Sta(() => {
        using var f = new Fixture(); f.Sync.Read = call => Task.FromResult<MusicTimedLyrics?>(f.Timing(call.Job));
        f.View.Dispose(); using var audio = new MusicAudioPlayer { Volume = 0 };
        using var view = new MusicWorkspaceControl(new(Path.Combine(f.Files.Root, "native-projects")), f.Jobs,
            new(Path.Combine(f.Files.Root, "native-output.json")), f.Sync, audio);
        view.Editor.Lyrics = Fixture.Lyrics;
        foreach (var track in new[] { f.A, f.B, f.A }) {
            view.Player.Select(track); Pump(() => audio.IsReady && Timing(view)?.JobId == track.JobId);
            Seek(view, .7); Assert.IsTrue(view.Player.CanHighlightPlayback);
            var start = track.JobId == f.A.JobId ? 0 : Fixture.First.Length + 1;
            var length = track.JobId == f.A.JobId ? Fixture.First.Length : Fixture.Second.Length;
            AssertHighlight(view, start, length);
            audio.Toggle(); view.Player.Pause(); AssertHighlight(view, start, length);
            audio.Toggle(); AssertHighlight(view, start, length); view.Player.Pause();
        }
        Assert.AreEqual(Fixture.Lyrics, view.Editor.Lyrics);
        view.Player.Clear(); Assert.IsNull(Timing(view)); AssertHighlight(view, 0, 0);
        Assert.IsTrue(File.Exists(f.A.Path)); Assert.IsTrue(File.Exists(f.B.Path));
    });

    [TestMethod, DataRow(false), DataRow(true)]
    public Task FinishingPreparationForAWhileBPlaysDoesNotRereadOrClearB(bool fail) => Sta(() => {
        using var f = new Fixture(); var completion = new TaskCompletionSource<MusicTimedLyrics?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyA = false;
        f.Sync.Read = call => Task.FromResult<MusicTimedLyrics?>(call.Job.Id == f.JobA.Id && !readyA ? null : f.Timing(call.Job));
        f.Sync.Analyze = (_, _, _) => completion.Task; f.View.LyricsPlayback.Configure(key => key);
        Select(f, f.A); Pump(() => f.Sync.Analyses == 1);
        Select(f, f.B); Pump(() => Timing(f.View)?.JobId == f.B.JobId);
        Seek(f.View, 1); f.Audio.Toggle(); var clock = Timing(f.View);
        AssertHighlight(f.View, Fixture.First.Length + 1, Fixture.Second.Length);
        readyA = true;
        if (fail) completion.SetException(new IOException("Other song preparation failed"));
        else completion.SetResult(f.Timing(f.JobA));
        Drain();
        Assert.AreEqual(1, f.Sync.Reads.Count(call => call.Job.Id == f.JobB.Id), "Unrelated completion reread the already verified B clock.");
        Assert.AreSame(clock, Timing(f.View)); Assert.IsTrue(f.Audio.IsPlaying);
        AssertHighlight(f.View, Fixture.First.Length + 1, Fixture.Second.Length);
        Assert.AreEqual(Fixture.Lyrics, f.View.Editor.Lyrics);
    });

    [TestMethod]
    public Task PauseNewHistoryRequestCancelAndResumeKeepTheSelectedFileClock() => Sta(() => {
        using var f = new Fixture(); f.Sync.Read = call => Task.FromResult<MusicTimedLyrics?>(f.Timing(call.Job));
        var recorded = f.View.Projects.Record(f.JobA, f.View.Projects.Capture());
        f.JobA = recorded;
        Select(f, f.A); Pump(() => Timing(f.View) is not null); Seek(f.View, 1); f.Audio.Toggle(); f.View.Player.Pause();
        var clock = Timing(f.View); AssertHighlight(f.View, 0, Fixture.First.Length);
        var attempt = f.Jobs.Create(f.Files.Root, f.Files.Root, "Cancelled attempt", 1, 12, "rock", Fixture.Lyrics);
        attempt = f.View.Projects.Record(attempt, f.View.Projects.Capture());
        f.View.LyricsPlayback.GenerationCanceled(attempt); f.View.Projects.Outcome(attempt, MusicProjectOutcome.Cancelled);
        Assert.AreEqual(attempt.Id, f.View.Projects.DisplayedLyricsJobId);
        Assert.AreSame(clock, Timing(f.View)); AssertHighlight(f.View, 0, Fixture.First.Length);
        f.Audio.Toggle(); AssertHighlight(f.View, 0, Fixture.First.Length);
        f.View.Editor.Lyrics += "!"; AssertHighlight(f.View, 0, 0);
        f.View.Editor.Lyrics = Fixture.Lyrics; AssertHighlight(f.View, 0, Fixture.First.Length);
        Assert.AreEqual(1, f.Sync.Reads.Count, "History operations must not reload the playing file.");
    });

    [TestMethod]
    public Task MissingSelectedClockWaitsForTheOperationGateAndAutomaticallyActivates() => Sta(() => {
        using var f = new Fixture();
        var controller = new BackgroundOperationController(new(Path.Combine(f.Files.Root, "background.json")));
        // The controller must exist when the workspace subscribes, as it does in the application host.
        f.View.Dispose(); ApplicationBackgroundOperations.Current = controller; f.RecreateView();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = ApplicationBackgroundOperations.RunAsync("music.generation", "Other generation", null, new { Test = true },
            async _ => { await hold.Task; return 1; }, default);
        var ready = false;
        f.Sync.Read = call => Task.FromResult<MusicTimedLyrics?>(ready ? f.Timing(call.Job) : null);
        f.Sync.Analyze = (jobId, _, _) => { ready = true; return Task.FromResult<MusicTimedLyrics?>(f.Timing(f.Jobs.Load(jobId))); };
        f.View.LyricsPlayback.Configure(key => key); Select(f, f.A); Seek(f.View, 1);
        Pump(() => f.Sync.Reads.Count == 1); Drain(); Assert.AreEqual(0, f.Sync.Analyses); AssertHighlight(f.View, 0, 0);
        hold.SetResult(); Pump(() => run.IsCompleted); run.GetAwaiter().GetResult();
        Pump(() => Timing(f.View) is not null); Assert.AreEqual(1, f.Sync.Analyses); AssertHighlight(f.View, 0, Fixture.First.Length);
    });

    [TestMethod]
    public Task AnOldUnavailableMarkerDoesNotBlockTheNewProviderAttempt() => Sta(() => {
        using var f = new Fixture(); var ready = false;
        f.Jobs.SetKaraokeReadiness(f.JobA.Id, 0, MusicKaraokeReadiness.Unavailable);
        var unavailable = f.Jobs.Tracks(f.JobA.Id).Single(); Assert.IsTrue(unavailable.KaraokeUnavailable);
        f.Sync.Read = call => Task.FromResult<MusicTimedLyrics?>(ready ? f.Timing(call.Job) : null);
        f.Sync.Analyze = (jobId, _, _) => { ready = true; return Task.FromResult<MusicTimedLyrics?>(f.Timing(f.Jobs.Load(jobId))); };
        f.View.LyricsPlayback.Configure(key => key); Select(f, unavailable); Seek(f.View, 1);
        Pump(() => Timing(f.View) is not null); Assert.AreEqual(1, f.Sync.Analyses);
        AssertHighlight(f.View, 0, Fixture.First.Length);
    });

    [TestMethod]
    public Task PersistedReadyRefreshRemovesWarningWithoutReopeningOrMovingThePlayer() => Sta(() => {
        using var f = new Fixture(); var ready = false;
        f.Jobs.SetKaraokeReadiness(f.JobA.Id, 0, MusicKaraokeReadiness.Unavailable);
        f.View.Tracks.Localize(key => key);
        f.Sync.Read = call => Task.FromResult<MusicTimedLyrics?>(ready ? f.Timing(call.Job) : null);
        f.Sync.Analyze = (jobId, index, _) => {
            f.Jobs.SetKaraokeReadiness(jobId, index, MusicKaraokeReadiness.Ready); ready = true;
            return Task.FromResult<MusicTimedLyrics?>(f.Timing(f.Jobs.Load(jobId)));
        };
        f.View.Tracks.AddTrack(f.Jobs.Tracks(f.JobA.Id).Single());
        Assert.IsTrue(HasUnavailableWarning(f.View)); var path = f.View.Player.CurrentTrack!.Path;
        var playerTrack = f.View.Player.CurrentTrack;
        Seek(f.View, 1); var selectionChanges = 0; var sourceChanges = 0;
        f.View.Tracks.Selected += (_, _) => selectionChanges++; f.View.Player.TrackChanged += _ => sourceChanges++;
        f.View.LyricsPlayback.Configure(key => key);
        Pump(() => Timing(f.View) is not null && !HasUnavailableWarning(f.View));
        Assert.AreEqual(0, selectionChanges); Assert.AreEqual(0, sourceChanges);
        Assert.AreSame(playerTrack, f.View.Player.CurrentTrack); Assert.AreEqual(path, f.View.Player.CurrentTrack!.Path);
        Assert.AreEqual(1, f.View.Player.PlaybackPosition.TotalSeconds);
        Assert.AreEqual(path, f.View.Tracks.SelectedTrack!.Path); Assert.IsFalse(f.View.Tracks.SelectedTrack.KaraokeUnavailable);
        AssertHighlight(f.View, 0, Fixture.First.Length);
    });

    [TestMethod]
    public Task AFixtureTimingDoesNotRemoveWarningWithoutPersistedReadyState() => Sta(() => {
        using var f = new Fixture(); f.Jobs.SetKaraokeReadiness(f.JobA.Id, 0, MusicKaraokeReadiness.Unavailable);
        f.Sync.Read = call => Task.FromResult<MusicTimedLyrics?>(f.Timing(call.Job));
        f.View.Tracks.Localize(key => key); f.View.Tracks.AddTrack(f.Jobs.Tracks(f.JobA.Id).Single());
        Pump(() => Timing(f.View) is not null); Assert.IsTrue(HasUnavailableWarning(f.View));
        Assert.IsTrue(f.View.Tracks.SelectedTrack!.KaraokeUnavailable);
        Assert.AreEqual(MusicKaraokeReadiness.Unavailable, f.Jobs.Load(f.JobA.Id).Variants[0].KaraokeReadiness);
    });

    [TestMethod]
    public Task SelectedSongAnalysisFailureReleasesTheCommonGateAndKeepsAudioPlayable() => Sta(() => {
        using var f = new Fixture(); var controller = new BackgroundOperationController(new(Path.Combine(f.Files.Root, "background.json")));
        f.View.Dispose(); ApplicationBackgroundOperations.Current = controller; f.RecreateView();
        var retired = new RetirementProbe(); ApplicationBackgroundOperations.RegisterModel(retired, probe => { probe.Count++; return Task.CompletedTask; });
        f.Sync.Analyze = (_, _, _) => Task.FromException<MusicTimedLyrics?>(new IOException("ASR worker failed"));
        f.View.Tracks.Localize(key => key); f.View.Tracks.AddTrack(f.A); Seek(f.View, 1);
        var playerTrack = f.View.Player.CurrentTrack; var changes = 0; f.View.Player.TrackChanged += _ => changes++;
        f.View.LyricsPlayback.Configure(key => key);
        Pump(() => f.Sync.Analyses == 1 && !controller.IsRunning && !Field<bool>(f.View.LyricsPlayback, "_syncing"));
        Assert.IsFalse(controller.HasPending, "A failed optional karaoke analysis left every model operation blocked.");
        Assert.AreEqual(BackgroundOperationPhase.Completed, controller.State!.Phase);
        Assert.IsTrue(retired.Count > 0, "The operation gate was freed without confirming model retirement.");
        Assert.AreEqual(MusicKaraokeReadiness.Unavailable, f.Jobs.Load(f.JobA.Id).Variants[0].KaraokeReadiness);
        Assert.IsTrue(HasUnavailableWarning(f.View)); Assert.IsNull(Timing(f.View));
        Assert.AreSame(playerTrack, f.View.Player.CurrentTrack); Assert.AreEqual(1, f.View.Player.PlaybackPosition.TotalSeconds); Assert.AreEqual(0, changes);
        f.Audio.Toggle(); Assert.IsTrue(f.View.Player.IsPlaying); f.View.Player.Pause();
        var next = ApplicationBackgroundOperations.RunAsync("music.generation", "Next operation", null, new { Test = true }, _ => Task.FromResult(42), default);
        Pump(() => next.IsCompleted); Assert.AreEqual(42, next.GetAwaiter().GetResult()); Assert.IsFalse(controller.HasPending);
    });

    [TestMethod]
    public Task PausedSelectedSongAnalysisRemainsResumableAndIsNotMarkedUnavailable() => Sta(() => {
        using var f = new Fixture(); var controller = new BackgroundOperationController(new(Path.Combine(f.Files.Root, "background.json")));
        f.View.Dispose(); ApplicationBackgroundOperations.Current = controller; f.RecreateView();
        var ready = false; var attempts = 0;
        f.Sync.Read = call => Task.FromResult<MusicTimedLyrics?>(ready ? f.Timing(call.Job) : null);
        f.Sync.Analyze = async (jobId, index, token) => {
            if (Interlocked.Increment(ref attempts) == 1) await Task.Delay(Timeout.Infinite, token);
            f.Jobs.SetKaraokeReadiness(jobId, index, MusicKaraokeReadiness.Ready); ready = true;
            return f.Timing(f.Jobs.Load(jobId));
        };
        f.View.Tracks.Localize(key => key); f.View.Tracks.AddTrack(f.A); Seek(f.View, 1); f.View.LyricsPlayback.Configure(key => key);
        Pump(() => f.Sync.Analyses == 1 && controller.IsRunning);
        Play(f.View); Assert.IsFalse(f.Audio.IsPlaying);
        var paused = controller.PauseAsync(); Pump(() => paused.IsCompleted); paused.GetAwaiter().GetResult();
        Assert.AreEqual(BackgroundOperationPhase.Paused, controller.State!.Phase); Assert.IsTrue(controller.State.UserPaused); Assert.IsTrue(controller.HasPending);
        Assert.AreNotEqual(MusicKaraokeReadiness.Unavailable, f.Jobs.Load(f.JobA.Id).Variants[0].KaraokeReadiness);
        Assert.IsFalse(HasUnavailableWarning(f.View)); Assert.IsNull(Timing(f.View));
        Assert.IsFalse(f.Audio.IsPlaying, "A paused synchronization must not autoplay.");
        var resumed = controller.ResumeAsync(default); Pump(() => resumed.IsCompleted); resumed.GetAwaiter().GetResult();
        Pump(() => Timing(f.View) is not null && !controller.IsRunning && !Field<bool>(f.View.LyricsPlayback, "_syncing"));
        Assert.AreEqual(2, attempts); Assert.AreEqual(BackgroundOperationPhase.Completed, controller.State.Phase); Assert.IsFalse(controller.HasPending);
        Assert.IsFalse(HasUnavailableWarning(f.View)); AssertHighlight(f.View, 0, Fixture.First.Length);
        Assert.IsTrue(f.Audio.IsPlaying, "The original play request must survive an explicit synchronization pause/resume.");
    });

    private static Task Sta(Action action) => ScenarioNavigationTests.Sta(() => {
        var old = ApplicationBackgroundOperations.Current; var oldExit = ApplicationBackgroundOperations.ExitToken;
        ApplicationBackgroundOperations.Current = null; ApplicationBackgroundOperations.ExitToken = default;
        try { action(); } finally { ApplicationBackgroundOperations.Current = old; ApplicationBackgroundOperations.ExitToken = oldExit; }
    });
    private static void Select(Fixture f, MusicTrack track) => f.View.Player.Select(track);
    private static void Play(MusicWorkspaceControl view) => ScenarioNavigationTests.LogicalDescendants(view.Player)
        .OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == "Music.Audio.Play")
        .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    private static string PlayerStatus(MusicWorkspaceControl view) => ScenarioNavigationTests.LogicalDescendants(view.Player)
        .OfType<TextBlock>().Single(text => AutomationProperties.GetAutomationId(text) == "Music.Audio.Readiness").Text;
    private static void Seek(MusicWorkspaceControl view, double seconds) =>
        ScenarioNavigationTests.LogicalDescendants(view.Player).OfType<Slider>()
            .Single(slider => AutomationProperties.GetAutomationId(slider) == "Music.Audio.Seek").Value = seconds;
    private static MusicTimedLyrics? Timing(MusicWorkspaceControl view) => Field<MusicTimedLyrics?>(view.LyricsPlayback, "_timing");
    private static string Log(MusicWorkspaceControl view) => ScenarioNavigationTests.LogicalDescendants(view.Status).OfType<TextBox>().Single().Text;
    private static bool HasUnavailableWarning(MusicWorkspaceControl view) => ScenarioNavigationTests.LogicalDescendants(view.Tracks)
        .OfType<TextBlock>().Any(text => AutomationProperties.GetAutomationId(text) == "Music.Audio.KaraokeUnavailable");
    private static void AssertHighlight(MusicWorkspaceControl view, int start, int length)
    {
        var adorner = Field<MusicLyricsPlaybackAdorner>(view.Editor, "_playbackHighlight");
        Assert.AreEqual(start, Field<int>(adorner, "_start")); Assert.AreEqual(length, Field<int>(adorner, "_length"));
    }
    private static T Field<T>(object target, string field) => (T)target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Drain() => Pump(() => false, 150, require: false);
    private static void Pump(Func<bool> done, int milliseconds = 5000, bool require = true)
    {
        var frame = new DispatcherFrame(); var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5) };
        timer.Tick += (_, _) => { if (done() || DateTime.UtcNow >= deadline) frame.Continue = false; };
        timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        if (require) Assert.IsTrue(done(), "Playback lifecycle operation did not finish.");
    }
    private sealed class Fixture : IDisposable
    {
        public const string First = "Кот зовёт солнце";
        public const string Second = "Солнце уже взошло";
        public const string Lyrics = First + "\n" + Second;
        public MusicProjectTests.Files Files { get; } = new();
        public MusicGenerationJobs Jobs { get; }
        public MusicGenerationJob JobA { get; set; }
        public MusicGenerationJob JobB { get; }
        public MusicTrack A { get; }
        public MusicTrack B { get; }
        public FakeAudio Audio { get; private set; } = new();
        public SyncStub Sync { get; } = new();
        public MusicWorkspaceControl View { get; private set; } = null!;
        public Fixture()
        {
            Jobs = new(Path.Combine(Files.Root, "jobs"));
            JobA = Create("A"); JobB = Create("B"); A = Jobs.Tracks(JobA.Id).Single(); B = Jobs.Tracks(JobB.Id).Single();
            RecreateView();
        }
        public void RecreateView()
        {
            Audio = new(); View = new(new(Path.Combine(Files.Root, "projects")), Jobs, new(Path.Combine(Files.Root, "output.json")), Sync, Audio);
            View.Editor.Lyrics = Lyrics;
        }
        private MusicGenerationJob Create(string title)
        {
            var job = Jobs.Create(Files.Root, Files.Root, title, 1, 12, "rock", Lyrics);
            MusicOutputTests.WriteWave(job.Variants[0].ResultPath);
            using var file = File.OpenRead(job.Variants[0].ResultPath);
            job = job with { Variants = [job.Variants[0] with { Completed = true, AudioHash = Convert.ToHexString(SHA256.HashData(file)), DurationSeconds = 12 }] };
            Jobs.Save(job); return job;
        }
        public MusicTimedLyrics Timing(MusicGenerationJob job)
        {
            var isA = job.Id == JobA.Id;
            var timing = new MusicTimedLyrics { JobId = job.Id, Variant = 0, ProjectId = job.ProjectId, ProjectStep = job.ProjectStep,
                SourceLyrics = Lyrics, LyricsHash = MusicTimedLyrics.HashLyrics(Lyrics), AudioPayloadHash = new string('a', 64),
                DurationSeconds = 12, Provider = "lifecycle-fixture", ProviderVersion = "1",
                Lines = [new(isA ? 0 : First.Length + 1, isA ? First.Length : Second.Length, .5, 2)] };
            timing.Validate(); return timing;
        }
        public void Dispose() { View.Dispose(); Files.Dispose(); }
    }
    private sealed record ReadCall(int Number, MusicGenerationJob Job, CancellationToken Token);
    private sealed class RetirementProbe { public int Count; }
    private sealed class SyncStub : IMusicLyricsSynchronization
    {
        private int _reads, _analyses;
        public ConcurrentQueue<ReadCall> Reads { get; } = new();
        public int Analyses => Volatile.Read(ref _analyses);
        public Func<ReadCall, Task<MusicTimedLyrics?>> Read { get; set; } = _ => Task.FromResult<MusicTimedLyrics?>(null);
        public Func<string, int, CancellationToken, Task<MusicTimedLyrics?>> Analyze { get; set; } = (_, _, _) => Task.FromResult<MusicTimedLyrics?>(null);
        public event Action<string>? Log { add { } remove { } }
        public Task<MusicTimedLyrics?> ReadAsync(MusicGenerationJob job, int index, CancellationToken token)
        {
            var call = new ReadCall(Interlocked.Increment(ref _reads), job, token); Reads.Enqueue(call); return Read(call);
        }
        public Task<MusicTimedLyrics?> SynchronizeAsync(string jobId, int index, CancellationToken token)
        { Interlocked.Increment(ref _analyses); return Analyze(jobId, index, token); }
    }
    private sealed class FakeAudio : IMusicAudioPlayer
    {
        public bool IsReady { get; private set; }
        public bool IsPlaying { get; private set; }
        public int ToggleCount { get; private set; }
        public TimeSpan Duration => TimeSpan.FromSeconds(12);
        public TimeSpan Position { get; private set; }
        public double Volume { get; set; } = .75;
        public event Action? Changed;
        public event Action<string>? Failed { add { } remove { } }
        public void Open(string? path) { IsReady = path is not null; IsPlaying = false; Position = TimeSpan.Zero; Changed?.Invoke(); }
        public void Toggle() { ToggleCount++; IsPlaying = !IsPlaying; Changed?.Invoke(); }
        public void Pause() { IsPlaying = false; Changed?.Invoke(); }
        public void Seek(TimeSpan position) { Position = position; }
        public void Dispose() { IsReady = IsPlaying = false; }
    }
}
