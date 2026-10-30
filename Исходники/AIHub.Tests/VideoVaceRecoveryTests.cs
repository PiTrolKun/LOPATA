using System.IO;
using AIHub.Models;
using AIHub.Services.VideoPreparation;

namespace AIHub.Tests;

[TestClass]
public sealed class VideoVaceRecoveryTests
{
    [TestMethod]
    public async Task AConfirmedTakeSurvivesNativeCrashAndElapsedTimeNeverRestartsFromZero()
    {
        var run = new VideoVaceRun { GenerationSeconds = 30 }; var attempts = 0; var commits = 0; var logs = new List<string>();
        await VideoVaceRecovery.RunAsync(run, 1, elapsed =>
        {
            attempts++; Assert.IsTrue(elapsed() >= 30);
            if (attempts == 1)
            {
                run.Takes.Add(new("confirmed", "item", "take.mp4", 7, 17, 0, ""));
                throw new VideoVaceWorkerCrashException("reaped native failure");
            }
            Assert.AreEqual("confirmed", run.Takes.Single().Id); Assert.AreEqual(1, run.RetryAttempts);
            run.Complete = true; return Task.CompletedTask;
        }, () => { commits++; return Task.CompletedTask; }, logs.Add, default);
        Assert.AreEqual(2, attempts); Assert.AreEqual(2, commits); Assert.IsTrue(run.GenerationSeconds >= 30);
        Assert.IsTrue(logs.Any(l => l.Contains("retry", StringComparison.Ordinal))); Assert.IsTrue(run.Complete);
    }

    [TestMethod]
    public async Task ExhaustedCrashBudgetPausesInsteadOfStartingAnUnboundedLoop()
    {
        var run = new VideoVaceRun { RetryAttempts = 2, GenerationSeconds = 50 }; var attempts = 0; var commits = 0;
        var error = await Assert.ThrowsExactlyAsync<BackgroundOperationWaitingException>(() => VideoVaceRecovery.RunAsync(run, 2,
            _ => { attempts++; throw new VideoVaceWorkerCrashException("native failure"); },
            () => { commits++; return Task.CompletedTask; }, _ => { }, default));
        Assert.AreEqual("VideoPrep.VaceRetriesExhausted", error.Message); Assert.AreEqual(1, attempts); Assert.AreEqual(1, commits);
        Assert.AreEqual(2, run.RetryAttempts); Assert.IsTrue(run.GenerationSeconds >= 50);
    }

    [TestMethod]
    public async Task ValidationAndComponentFailuresNeverUseTheAutomaticCrashRetryBudget()
    {
        var run = new VideoVaceRun(); var attempts = 0; var commits = 0;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => VideoVaceRecovery.RunAsync(run, 5,
            _ => { attempts++; throw new InvalidDataException("component audit failed"); },
            () => { commits++; return Task.CompletedTask; }, _ => { }, default));
        Assert.AreEqual(1, attempts); Assert.AreEqual(1, commits); Assert.AreEqual(0, run.RetryAttempts);
        Assert.IsFalse(run.Complete); Assert.IsTrue(run.GenerationSeconds >= 0);
    }

    [TestMethod]
    public async Task ExplicitCancellationCommitsOnceAndIsNeverRetried()
    {
        using var cancel = new CancellationTokenSource(); var attempts = 0; var commits = 0; var run = new VideoVaceRun();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => VideoVaceRecovery.RunAsync(run, 5,
            _ => { attempts++; cancel.Cancel(); throw new OperationCanceledException(cancel.Token); },
            () => { commits++; return Task.CompletedTask; }, _ => { }, cancel.Token));
        Assert.AreEqual(1, attempts); Assert.AreEqual(1, commits); Assert.AreEqual(0, run.RetryAttempts); Assert.IsFalse(run.Complete);
    }
}
