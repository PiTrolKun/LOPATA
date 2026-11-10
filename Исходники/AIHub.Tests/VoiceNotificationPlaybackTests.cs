using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VoiceNotifications;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AIHub.Tests;

public sealed partial class VoiceNotificationTests
{
    [TestMethod]
    public Task ErrorInterruptsSpeechAndStatusesWaitWithoutDelayingUrgentPreemption() => OnUi(async () =>
    {
        using var fixture = new Fixture(); using var audit = ComponentAudit.UseTestStore(Path.Combine(fixture.Root, "audit"), false);
        var audio = new FakeAudio(); using var service = new VoiceNotificationService(new(), fixture.Store, Dispatcher.CurrentDispatcher, audio);
        await service.SetLanguageAsync("en"); Assert.IsNull(service.PackageError);
        var paused = 0; var resumed = 0;
        using var speech = VoicePlaybackHub.RegisterSpeech(() => paused++, () => resumed++);
        service.Publish(new("a", "job", [])); service.Publish(new("b", "job", []));
        await Task.Delay(30); Assert.AreEqual(0, audio.Started.Count);
        service.Publish(new("error", "job", []));
        await Until(() => audio.Started.Count == 1); Assert.AreEqual("voice.error", audio.Started[0].Clip); Assert.AreEqual(1, paused);
        audio.Finish(); await Task.Delay(560);
        Assert.AreEqual(1, resumed); Assert.AreEqual(1, audio.Started.Count);
        speech.Dispose(); await Until(() => audio.Started.Count == 2); Assert.AreEqual("voice.a", audio.Started[1].Clip);
        var urgent = Stopwatch.StartNew(); service.Publish(new("error", "another-job", []));
        await Until(() => audio.Started.Count == 3);
        Assert.IsLessThan(400d, urgent.Elapsed.TotalMilliseconds); Assert.AreEqual("voice.error", audio.Started[2].Clip);
        service.Settings.Enabled = false; service.Configure(); await Task.Delay(30); Assert.AreEqual(0, service.PendingCount);
    });

    [TestMethod]
    public Task CompletedPhrasesHaveHalfSecondGapAndFinishedEventsHaveNoCooldown() => OnUi(async () =>
    {
        using var fixture = new Fixture(); using var audit = ComponentAudit.UseTestStore(Path.Combine(fixture.Root, "audit"), false);
        var audio = new FakeAudio(); using var service = new VoiceNotificationService(new(), fixture.Store, Dispatcher.CurrentDispatcher, audio);
        await service.SetLanguageAsync("en");
        service.Publish(new("a", "job", [])); await Until(() => audio.Started.Count == 1);
        service.Publish(new("b", "job", [])); var gap = Stopwatch.StartNew(); audio.Finish();
        await Task.Delay(300); Assert.AreEqual(1, audio.Started.Count);
        await Until(() => audio.Started.Count == 2); Assert.IsGreaterThanOrEqualTo(450d, gap.Elapsed.TotalMilliseconds);
        audio.Finish(); await Task.Delay(30);
        service.Publish(new("b", "job", [])); await Until(() => audio.Started.Count == 3);
        var urgent = Stopwatch.StartNew(); service.Publish(new("a", "other", [])); await Until(() => audio.Started.Count == 4);
        Assert.IsLessThan(400d, urgent.Elapsed.TotalMilliseconds); Assert.AreEqual("voice.a", audio.Started[3].Clip);
        service.Settings.Volume = .2; service.Settings.Speed = 1.25; service.Configure();
        Assert.AreEqual(.2, audio.Volume); Assert.AreEqual(1.25, audio.Speed);
        service.Settings.Enabled = false; service.Configure();
    });

    [TestMethod]
    public Task FilteredEventsIncludingErrorsAreNotSavedForAfterMusic() => OnUi(async () =>
    {
        using var fixture = new Fixture(); using var audit = ComponentAudit.UseTestStore(Path.Combine(fixture.Root, "audit"), false);
        var audio = new FakeAudio(); using var service = new VoiceNotificationService(new(), fixture.Store, Dispatcher.CurrentDispatcher, audio);
        await service.SetLanguageAsync("en"); var media = new object();
        try
        {
            VoicePlaybackHub.MediaState(media, true, "project:current");
            Assert.IsFalse(service.Publish(new("error", "other-job", ["project:other"])));
            Assert.IsTrue(service.Publish(new("a", "current-job", ["project:current"])));
            await Until(() => audio.Started.Count == 1); audio.Finish(); await Task.Delay(30);
            VoicePlaybackHub.MediaState(media, false); await Task.Delay(80);
            Assert.AreEqual(1, audio.Started.Count); Assert.AreEqual(0, service.PendingCount);
        }
        finally { VoicePlaybackHub.MediaState(media, false); }
    });

    private sealed class FakeAudio : IVoiceNotificationAudio
    {
        internal List<(string Clip, DateTimeOffset At)> Started { get; } = [];
        private TaskCompletionSource? _ended;
        internal double Volume, Speed;
        public void Configure(double volume, double speed) { Volume = volume; Speed = speed; }
        public async Task PlayAsync(VoiceNotificationPackage package, VoiceAudioItem item, double volume, double speed, CancellationToken token)
        {
            Configure(volume, speed); Started.Add((item.ClipId, DateTimeOffset.UtcNow));
            var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); _ended = ended;
            await ended.Task.WaitAsync(token);
        }
        internal void Finish() => _ended?.TrySetResult();
        public void Dispose() => _ended?.TrySetCanceled();
    }
    private static async Task Until(Func<bool> predicate)
    {
        var clock = Stopwatch.StartNew();
        while (!predicate()) { if (clock.Elapsed > TimeSpan.FromSeconds(3)) Assert.Fail("Notification state timed out."); await Task.Delay(10); }
    }
    private static readonly Lazy<Dispatcher> Ui = new(() =>
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher)); ready.SetResult(dispatcher); Dispatcher.Run(); }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return ready.Task.GetAwaiter().GetResult();
    });
    private static Task OnUi(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Ui.Value.BeginInvoke(async () => { try { await action(); done.TrySetResult(); } catch (Exception error) { done.TrySetException(error); } });
        return done.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
