using AIHub.Models;
using Lopata.Island;

namespace AIHub.Services;

internal sealed class IslandHostCommands(BackgroundOperationController operations,
    Func<string, Task<bool>> resumeJob, Func<string, CancellationToken, Task<string>> receiveDraft,
    Func<BackgroundOperationState, double?>? progress = null, Func<bool>? chatActive = null)
{
    private readonly Queue<string> acceptedDrafts = new();

    internal async Task<IslandResponse> HandleAsync(IslandRequest request, CancellationToken token)
    {
        IslandResponse Snapshot(string result = "ok")
        {
            var pending = operations.PendingOperations;
            var jobs = pending.Select(job => new IslandJob(job.Id, job.Title, job.Phase.ToString(),
                CanPause(job), operations.CanResumeFromIsland(job.Id), progress?.Invoke(job))).ToArray();
            var blocking = pending.FirstOrDefault(job => job.Id == operations.State?.Id && CanPause(job))
                ?? pending.FirstOrDefault(CanPause);
            bool working = chatActive?.Invoke() == true || operations.AuxiliaryState.Active ||
                pending.Any(job => (job.Phase is BackgroundOperationPhase.Running or BackgroundOperationPhase.Pausing)
                    && operations.HasLiveOperation(job.Id));
            return new(result, jobs, operations.BlocksNewWork, blocking?.Id,
                ModelActive: working, ModelFailure: operations.RecentIslandFailure());
        }

        async Task<bool> Pause(string id)
        {
            if (!operations.PendingOperations.Any(job => job.Id == id && CanPause(job))) return false;
            await operations.PauseAsync(id).WaitAsync(token);
            return operations.PausedOperations.Any(job => job.Id == id && job.Phase == BackgroundOperationPhase.Paused);
        }

        if (request.Action == "status") return Snapshot();
        if (request.Action == "pause" && request.Id is { } pause)
            return Snapshot(await Pause(pause) ? "ok" : "changed");
        if (request.Action == "resume" && request.Id is { } resume)
        {
            if (!operations.CanResumeFromIsland(resume)) return Snapshot("changed");
            var resumed = resumeJob(resume);
            while (operations.PausedOperations.Any(job => job.Id == resume))
            {
                if (resumed.IsCompleted && !await resumed) return Snapshot("changed");
                await Task.Delay(60, token);
            }
            var resumedState = operations.FindIslandOperation(resume);
            return Snapshot(resumedState?.Phase is BackgroundOperationPhase.Running or BackgroundOperationPhase.Completed ? "ok" : "changed");
        }
        if (request.Action != "draft" || !Guid.TryParseExact(request.Id, "N", out _)
            || request.Text is not { Length: > 0 and <= IslandLink.MaximumDraft } text || string.IsNullOrWhiteSpace(text))
            return new("invalid");
        if (acceptedDrafts.Contains(request.Id!)) return Snapshot("transferred");
        if (operations.BlocksNewWork)
        {
            var blocking = Snapshot("busy");
            if (request.PauseId is null || request.PauseId != blocking.BlockingId) return blocking;
            if (!await Pause(request.PauseId) || operations.BlocksNewWork) return Snapshot("changed");
        }

        token.ThrowIfCancellationRequested();
        var result = await receiveDraft(text, token);
        if (result != "transferred") return Snapshot(result);
        acceptedDrafts.Enqueue(request.Id!);
        if (acceptedDrafts.Count > 64) acceptedDrafts.Dequeue();
        return Snapshot("transferred");
    }

    private static bool CanPause(BackgroundOperationState job) => !job.UserPaused && !job.RequiresDecision &&
        job.Phase is BackgroundOperationPhase.Running or BackgroundOperationPhase.Countdown or BackgroundOperationPhase.Waiting;
}
