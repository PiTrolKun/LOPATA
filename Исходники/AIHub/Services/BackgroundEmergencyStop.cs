using AIHub.Models;
using System.Runtime.CompilerServices;

namespace AIHub.Services;

public sealed record EmergencyStopTask(string Id, string Title, string Kind, BackgroundOperationPhase Phase);
public sealed record EmergencyStopWorker(int Pid, string Component, DateTime Started);
public sealed record EmergencyStopPlan(IReadOnlyList<EmergencyStopTask> Tasks, IReadOnlyList<EmergencyStopWorker> Workers)
{
    public bool HasAuxiliary { get; init; }
    internal long AuxiliaryVersion { get; init; }
}

/// <summary>Stops only reviewed application jobs/workers; keeps every checkpoint and result.</summary>
public static class BackgroundEmergencyStop
{
    private static readonly TimeSpan PhaseTimeout = TimeSpan.FromSeconds(15);
    private static readonly ConditionalWeakTable<BackgroundOperationController, UnsettledWork> PreviousAttempts = new();
    private sealed class UnsettledWork
    {
        private readonly object _gate = new();
        private readonly List<Task> _tasks = [];
        internal Task[] Snapshot()
        {
            lock (_gate) { _tasks.RemoveAll(task => task.IsCompleted); return _tasks.ToArray(); }
        }
        internal Task Track(Task task)
        {
            lock (_gate) { _tasks.RemoveAll(previous => previous.IsCompleted); _tasks.Add(task); }
            return task;
        }
    }

    public static EmergencyStopPlan Capture(BackgroundOperationController controller, OwnedProcessRegistry registry)
    {
        var auxiliary = controller.AuxiliaryState;
        return new(controller.PendingOperations.Select(state => new EmergencyStopTask(state.Id, state.Title, state.Kind, state.Phase)).ToArray(),
            registry.EmergencyWorkers()) { HasAuxiliary = auxiliary.Active, AuxiliaryVersion = auxiliary.Version };
    }

    public static Task ExecuteAsync(BackgroundOperationController controller, OwnedProcessRegistry registry,
        EmergencyStopPlan plan, Func<Task> releaseModels, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(releaseModels);
        // Checkpoint I/O, native termination and model retirement must never block WPF.
        return Task.Run(() => ExecuteCoreAsync(controller, registry, plan, releaseModels, token), CancellationToken.None);
    }

    private static async Task ExecuteCoreAsync(BackgroundOperationController controller, OwnedProcessRegistry registry,
        EmergencyStopPlan plan, Func<Task> releaseModels, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Freeze starts first, then validate jobs: no unreviewed work may enter between
        // confirmation and cancellation. Validation failure has no destructive effects.
        var workers = registry.FreezeForEmergencyStop(plan.Workers);
        BackgroundOperationController.EmergencyLease? jobs = null;
        Task cancellation = Task.CompletedTask;
        var unsettled = PreviousAttempts.GetValue(controller, _ => new());
        var previousWork = unsettled.Snapshot();
        try
        {
            jobs = controller.BeginEmergencyStop(plan);
            workers.MarkStopping();
            cancellation = unsettled.Track(jobs.RequestAsync());
            await BoundedAsync(unsettled.Track(workers.StopAndWaitAsync()), token).ConfigureAwait(false);
            // A timeout cannot cancel arbitrary backend cleanup. A retry must wait
            // for it to settle before another retirement can release the gate.
            try { await BoundedAsync(Task.WhenAll(previousWork), token).ConfigureAwait(false); }
            catch when (!token.IsCancellationRequested && previousWork.All(work => work.IsCompleted)) { }
            await BoundedAsync(unsettled.Track(Task.Run(releaseModels, CancellationToken.None)), token).ConfigureAwait(false);
            await BoundedAsync(cancellation, token).ConfigureAwait(false);
            await BoundedAsync(unsettled.Track(jobs.IdleAsync()), token).ConfigureAwait(false);
            jobs.Complete();
            workers.Complete();
        }
        catch
        {
            // Observe late cancellation callback faults after a bounded wait. Both
            // leases retain blocking state on failure; a failed stop never frees GPU.
            _ = cancellation.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
        finally
        {
            // Keep the scheduler reset gate until the registry has released its
            // freeze; a newly admitted task must never see a half-released reset.
            workers.Dispose();
            jobs?.Dispose();
        }
    }

    private static async Task BoundedAsync(Task work, CancellationToken token)
    {
        try { await work.WaitAsync(PhaseTimeout, token).ConfigureAwait(false); }
        catch
        {
            _ = work.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }
}
