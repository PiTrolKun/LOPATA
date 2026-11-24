using System.Text.Json;
using AIHub.Models;
using AIHub.Services.ExternalAgents;

namespace AIHub.Tests;

[TestClass]
public sealed class ExternalAgentEnvironmentTests
{
    private static readonly AgentEnvironmentRequest Prepare = new("environment_prepare", "animated_assets", "artist");
    private static string State(AgentEnvironmentJobs jobs, string action) =>
        JsonSerializer.SerializeToElement(jobs.Handle(Prepare with { Action = action }), AgentProtocol.Json).GetProperty("state").GetString()!;

    [TestMethod]
    public async Task TwoClientsShareDownloadAndCanObserveConsentProgressAndCancellation()
    {
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var downloading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int downloads = 0;
        var jobs = new AgentEnvironmentJobs(() => new(), () => 2, CancellationToken.None,
            async (_, _, _, _, download, progress, token) =>
            {
                if (!download) return false;
                Interlocked.Increment(ref downloads);
                AgentEnvironmentJobs.UserDecision.Value!(true);
                waiting.SetResult();
                await confirmed.Task.WaitAsync(token);
                AgentEnvironmentJobs.UserDecision.Value!(false);
                progress.Report(new("artist", "model", 10, 20, 5, "downloading"));
                downloading.SetResult();
                await Task.Delay(Timeout.Infinite, token);
                return true;
            });
        State(jobs, "environment_prepare");
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual("waiting_for_consent", State(jobs, "environment_prepare"));
        confirmed.SetResult();
        await downloading.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var status = JsonSerializer.SerializeToElement(jobs.Handle(Prepare with { Action = "environment_status" }), AgentProtocol.Json);
        Assert.AreEqual(10, status.GetProperty("progress").GetProperty("downloadedBytes").GetInt64());
        State(jobs, "environment_cancel");
        Assert.IsTrue(SpinWait.SpinUntil(() => State(jobs,"environment_status") == "cancelled", TimeSpan.FromSeconds(5)));
        Assert.AreEqual(1, downloads);
    }

    [TestMethod]
    public void ReadyFilesSkipDownloadAndCheckOnlyStopsAtMissingFiles()
    {
        foreach (bool ready in new[] { true, false })
        {
            int downloads = 0;
            var jobs = new AgentEnvironmentJobs(() => new StorageSettings(), () => 1, CancellationToken.None,
                (_, _, _, _, download, _, _) => { if (download) Interlocked.Increment(ref downloads); return Task.FromResult(ready); });
            State(jobs, ready ? "environment_prepare" : "environment_check");
            Assert.IsTrue(SpinWait.SpinUntil(() => State(jobs,"environment_status") == (ready ? "ready" : "missing"), TimeSpan.FromSeconds(5)));
            Assert.AreEqual(0, downloads);
        }
    }
}
