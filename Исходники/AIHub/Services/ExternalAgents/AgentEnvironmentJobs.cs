using AIHub.Models;

namespace AIHub.Services.ExternalAgents;

internal sealed class AgentConsentDeclinedException() : OperationCanceledException("Component terms were declined.") { }

internal delegate Task<bool> PrepareAgentModels(StorageSettings storage, int connections, string scenario, string operation,
    bool download, IProgress<ManagedModelDownloadProgress> progress, CancellationToken token);

internal sealed class AgentEnvironmentJobs(Func<StorageSettings> storage, Func<int> connections, CancellationToken lifetime,
    PrepareAgentModels? prepare = null)
{
    private readonly PrepareAgentModels _prepare = prepare ?? AgentModelPlan.PrepareAsync;
    private sealed class Job
    {
        public required string Scenario { get; init; }
        public required string Operation { get; init; }
        public required CancellationTokenSource Stop { get; init; }
        public bool Download { get; set; }
        public bool Running { get; set; } = true;
        public string State { get; set; } = "checking";
        public string? Reason { get; set; }
        public ManagedModelDownloadProgress? Progress { get; set; }
    }

    internal static readonly AsyncLocal<Action<bool>?> UserDecision = new();
    private readonly Dictionary<(string, string), Job> _jobs = new();
    private readonly object _sync = new();

    internal object Handle(AgentEnvironmentRequest r)
    {
        lock (_sync)
        {
            var key = (r.Scenario, r.Operation);
            _jobs.TryGetValue(key, out var job);
            if (r.Action == "environment_cancel" && job?.Running == true) job.Stop.Cancel();
            if (r.Action is "environment_cancel" or "environment_status")
                return job is null ? new { r.Scenario, r.Operation, state = "not_started" } : Snapshot(job);
            if (r.Action is not ("environment_prepare" or "environment_check")) throw new ArgumentException("Unknown action.");
            if (job?.Running == true)
            {
                job.Download |= r.Action == "environment_prepare";
                return Snapshot(job);
            }
            if (_jobs.Values.FirstOrDefault(j => j.Running) is { } active)
                return new { state = "busy", active.Scenario, active.Operation };
            job = new Job { Scenario = r.Scenario, Operation = r.Operation,
                Download = r.Action == "environment_prepare", Stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime) };
            _jobs[key] = job;
            _ = Task.Run(() => RunAsync(job));
            return Snapshot(job);
        }
    }

    private static object Snapshot(Job job) => new { job.Scenario, job.Operation, job.State, job.Reason,
        progress = job.Progress is { } p ? new { p.ModelArtifactId, p.Stage, p.DownloadedBytes, p.TotalBytes, p.BytesPerSecond } : null };

    private sealed class ProgressSink(Action<ManagedModelDownloadProgress> report) : IProgress<ManagedModelDownloadProgress>
    {
        public void Report(ManagedModelDownloadProgress value) => report(value);
    }

    private async Task RunAsync(Job job)
    {
        try
        {
            var settings = storage();
            int parallel = connections();
            var progress = new ProgressSink(p => { lock (_sync) job.Progress = p; });
            var activeState = "checking";
            UserDecision.Value = waiting => { lock (_sync) job.State = waiting ? "waiting_for_consent" : activeState; };
            var ready = await _prepare(settings, parallel, job.Scenario, job.Operation, false, progress, job.Stop.Token);
            lock (_sync)
            {
                if (ready || !job.Download)
                {
                    Finish(job, ready ? "ready" : "missing");
                    return;
                }
                job.State = activeState = "preparing";
            }
            ready = await _prepare(settings, parallel, job.Scenario, job.Operation, true, progress, job.Stop.Token);
            Finish(job, ready ? "ready" : "missing");
        }
        catch (AgentConsentDeclinedException) { Finish(job, "declined"); }
        catch (OperationCanceledException) { Finish(job, "cancelled"); }
        catch (BackgroundOperationWaitingException error) { Finish(job, "waiting_for_user", error.Message); }
        catch (Exception error) { Finish(job, "error", error.Message); }
        finally { UserDecision.Value = null; }
    }

    private void Finish(Job job, string state, string? reason = null)
    {
        lock (_sync)
        {
            job.State = state;
            job.Reason = reason;
            job.Running = false;
            job.Stop.Dispose();
        }
    }
}
