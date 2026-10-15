using AIHub.Models;

namespace AIHub.Services;

/// <summary>One compute lease, independent durable jobs. A fully retired pause releases the lease.</summary>
public sealed class BackgroundOperationController(BackgroundOperationStore store)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, BackgroundOperationExecution> _jobs = [];
    private readonly Dictionary<string, Func<BackgroundOperationState, CancellationToken, Task>> _restorers = [];
    private readonly Dictionary<string, BackgroundOperationNotice> _notices = [];
    private readonly HashSet<string> _acknowledged = [];
    private BackgroundOperationExecution? _selected;
    private bool _auxiliary, _loadFailed;
    private string? _resumingId;
    private TaskCompletionSource _auxiliaryIdle = CompletedSource();
    public event Action? Changed;
    public event Action<BackgroundOperationNotice>? Completed;

    private static TaskCompletionSource CompletedSource()
    { var value = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); value.SetResult(); return value; }
    private static bool Final(BackgroundOperationState? state) => state?.Phase is BackgroundOperationPhase.Completed or BackgroundOperationPhase.Failed or BackgroundOperationPhase.Canceled;
    private static bool Parked(BackgroundOperationExecution job) => job.State is { UserPaused: true, RequiresDecision: false, Phase: BackgroundOperationPhase.Paused }
        || !job.IsRunning && job.State is { UserPaused: true, RequiresDecision: false, Phase: BackgroundOperationPhase.Waiting or BackgroundOperationPhase.Countdown };
    private bool Busy => _auxiliary || _resumingId is not null || _jobs.Values.Any(job => job.IsRunning && !Parked(job));
    public bool IsRunning { get { lock (_gate) return Busy; } }
    public bool BlocksNewWork { get { lock (_gate) return _loadFailed || Busy || _jobs.Values.Any(job => job.HasPending && !Parked(job)); } }
    public bool HasPending { get { lock (_gate) return _selected?.HasPending == true; } }
    public bool CanRestore { get { lock (_gate) return _selected?.State is { } state && _restorers.ContainsKey(state.Kind); } }
    internal bool IsInOperationScope { get { lock (_gate) return _jobs.Values.Any(job => job.IsInOperationScope); } }
    public BackgroundOperationState? State { get { lock (_gate) return VisibleState(); } }
    public IReadOnlyList<BackgroundOperationState> PausedOperations
    { get { lock (_gate) return _jobs.Values.Where(job => job.HasPending && Parked(job)).Select(job => job.State!).OrderBy(state => state.UpdatedUtc).ToArray(); } }
    public BackgroundOperationState? LoadResult(string id) => store.LoadResult(id);
    public bool HasLiveOperation(string id) { lock (_gate) return _jobs.GetValueOrDefault(id)?.IsRunning == true; }

    private BackgroundOperationState? VisibleState()
    {
        if (_selected?.State is not { } state) return null;
        var notices = _notices.Values.ToArray();
        return state with { Notices = notices, Notice = notices.LastOrDefault(), NeedsAttention = notices.Length > 0 || state.NeedsAttention && state.Notice is null };
    }
    private void Collect(BackgroundOperationState? state)
    {
        if (state is null) return;
        foreach (var notice in state.Notices.Concat(state.Notice is null ? [] : new[] { state.Notice }))
            if (!_acknowledged.Contains(notice.Id)) _notices[notice.Id] = notice;
    }
    private BackgroundOperationExecution Add(BackgroundOperationState state, bool load)
    {
        var destination = store.Operation(state.Id);
        if (load) destination.Save(state);
        var execution = new BackgroundOperationExecution(destination);
        foreach (var (kind, restore) in _restorers) execution.Register(kind, restore);
        if (load) execution.Load();
        _jobs.Add(state.Id, execution);
        execution.Changed += () => Update(execution);
        execution.Completed += notice => {
            foreach (Action<BackgroundOperationNotice> listener in Completed?.GetInvocationList() ?? [])
                try { listener(notice); } catch (Exception error) { OwnedProcessRegistry.Log("background_observer_failed", "Application", detail: error.GetType().Name); }
        };
        return execution;
    }
    private void Update(BackgroundOperationExecution execution)
    {
        lock (_gate)
        {
            Collect(execution.State);
            if (execution.State is { } current && current.Id == _resumingId && current.Phase != BackgroundOperationPhase.Paused) _resumingId = null;
            if (ReferenceEquals(execution, _selected) && VisibleState() is { } state) store.Save(state);
        }
        Notify();
    }
    private void Notify()
    {
        foreach (Action listener in Changed?.GetInvocationList() ?? [])
            try { listener(); } catch (Exception error) { OwnedProcessRegistry.Log("background_observer_failed", "Application", detail: error.GetType().Name); }
    }
    public void Register(string kind, Func<BackgroundOperationState, CancellationToken, Task> restore)
    { lock (_gate) { _restorers.Add(kind, restore); foreach (var job in _jobs.Values) job.Register(kind, restore); } }
    public BackgroundOperationState? FindOperation(string kind)
    { lock (_gate) return _selected?.State?.Kind == kind ? _selected.State : _jobs.Values.Select(job => job.State).LastOrDefault(state => state?.Kind == kind && !Final(state)); }
    public void Load()
    {
        lock (_gate)
        {
            if (Busy || _jobs.Values.Any(job => job.IsRunning)) throw new InvalidOperationException("Cannot reload running operations.");
            try
            {
                var selected = store.Load();
                // Legacy callers can explicitly update operation.json. A later write there must win;
                // a job checkpoint written just before a crash can, conversely, be newer than the mirror.
                var selectedIsNewer = selected is not null && store.LastWriteUtc >= store.Operation(selected.Id).LastWriteUtc;
                var states = store.LoadOperations().Concat(selected is null ? [] : new[] { selected })
                    .GroupBy(state => state.Id).Select(group => selectedIsNewer && group.Key == selected!.Id
                        ? selected! : group.MaxBy(state => state.UpdatedUtc)!).ToArray();
                _jobs.Clear(); _notices.Clear();
                foreach (var state in states.Where(state => state.Id == selected?.Id || !Final(state))) { Collect(state); Add(state, true); }
                _selected = selected is not null && _jobs.TryGetValue(selected.Id, out var execution) ? execution : _jobs.Values.LastOrDefault();
                if (_selected?.HasPending != true || Parked(_selected))
                    _selected = _jobs.Values.FirstOrDefault(job => job.HasPending && !Parked(job)) ?? _selected;
                _loadFailed = false;
                if (VisibleState() is { } restored) store.Save(restored);
            }
            catch { _loadFailed = true; throw; }
        }
        Notify();
    }
    public void CheckpointForExit()
    {
        BackgroundOperationExecution[] jobs; lock (_gate) jobs = _jobs.Values.ToArray();
        foreach (var job in jobs) job.CheckpointForExit();
    }
    public void SaveCheckpoint<T>(T checkpoint)
    {
        BackgroundOperationExecution? execution; lock (_gate) execution = _jobs.Values.SingleOrDefault(job => job.IsInOperationScope);
        if (execution is null) throw new InvalidOperationException("No matching operation scope.");
        execution.SaveCheckpoint(checkpoint);
    }
    public async Task<T> RunAsync<T>(BackgroundOperationState input, Func<CancellationToken, Task<T>> execute, Func<Task> releaseModels, CancellationToken lifetime)
    {
        BackgroundOperationExecution execution = null!;
        Task<T> running;
        async Task Release()
        {
            if (execution.State is { UserPaused: true, Phase: BackgroundOperationPhase.Paused or BackgroundOperationPhase.Waiting }) return;
            await releaseModels();
        }
        lock (_gate)
        {
            if (_jobs.Values.FirstOrDefault(job => job.IsInOperationScope) is { } nested) execution = nested;
            else
            {
                if (_loadFailed || _auxiliary || _resumingId is not null && _resumingId != input.Id
                    || _jobs.Values.Any(job => job.IsRunning && !Parked(job))
                    || _jobs.Values.Any(job => job.HasPending && !Parked(job) && job.State?.Id != input.Id))
                    throw new InvalidOperationException("Another background operation requires attention or is running.");
                Collect(_selected?.State);
                execution = _jobs.TryGetValue(input.Id, out var previous) ? previous : Add(input, false);
                _selected = execution;
                input = input with { Notices = _notices.Values.ToArray(), Notice = _notices.Values.LastOrDefault(), NeedsAttention = _notices.Count > 0 || input.NeedsAttention };
            }
            // Enter the execution synchronously while holding the scheduler gate: no two starts can acquire it.
            running = execution.RunAsync(input, execute, Release, lifetime);
        }
        return await running;
    }
    public IDisposable BeginAuxiliary()
    {
        lock (_gate)
        {
            if (BlocksNewWork) throw new InvalidOperationException("Music.Poetry.OtherOperation");
            _auxiliary = true; _auxiliaryIdle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        Notify(); return new Lease(this);
    }
    private sealed class Lease(BackgroundOperationController owner) : IDisposable
    {
        private BackgroundOperationController? _owner = owner;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is not { } controller) return;
            lock (controller._gate) { controller._auxiliary = false; controller._auxiliaryIdle.TrySetResult(); }
            controller.Notify();
        }
    }
    public Task WaitForIdleAsync()
    {
        lock (_gate) return Task.WhenAll(_jobs.Values.Where(job => !Parked(job) || !_auxiliary && ReferenceEquals(job, _selected))
            .Select(job => job.WaitForIdleAsync()).Append(_auxiliaryIdle.Task));
    }
    public Task WaitForAllIdleAsync()
    { lock (_gate) return Task.WhenAll(_jobs.Values.Select(job => job.WaitForIdleAsync()).Append(_auxiliaryIdle.Task)); }
    public Task WaitForIdleAsync(string id)
    { lock (_gate) return _jobs.GetValueOrDefault(id)?.WaitForIdleAsync() ?? Task.CompletedTask; }
    public Task PauseAsync()
    { BackgroundOperationExecution? job; lock (_gate) job = _selected; return job?.PauseAsync() ?? Task.CompletedTask; }
    public Task ResumeAsync(CancellationToken token) => ResumeAsync(null, token);
    public async Task ResumeAsync(string? id, CancellationToken token)
    {
        BackgroundOperationExecution? job;
        lock (_gate)
        {
            job = id is null ? _selected : _jobs.GetValueOrDefault(id);
            if (job?.HasPending != true) return;
            if (_resumingId == job.State!.Id) return;
            if (_resumingId is not null) throw new InvalidOperationException("Music.Poetry.OtherOperation");
            if (job.State?.Phase == BackgroundOperationPhase.Running) return;
            if (_auxiliary || _jobs.Values.Any(other => !ReferenceEquals(other, job) && (other.IsRunning && !Parked(other) || other.HasPending && !Parked(other))))
                throw new InvalidOperationException("Music.Poetry.OtherOperation");
            _selected = job;
            _resumingId = job.State!.Id; store.Save(VisibleState()!);
        }
        Notify();
        try { await job.ResumeAsync(token); }
        finally { lock (_gate) if (_resumingId == job.State!.Id && (!job.IsRunning || job.State.Phase != BackgroundOperationPhase.Paused)) _resumingId = null; Notify(); }
    }
    public void SetCountdown(bool enabled) { BackgroundOperationExecution? job; lock (_gate) job = _selected; job?.SetCountdown(enabled); }
    public void Acknowledge(string id)
    {
        BackgroundOperationExecution[] jobs;
        lock (_gate) { _acknowledged.Add(id); _notices.Remove(id); jobs = _jobs.Values.ToArray(); }
        foreach (var job in jobs) job.Acknowledge(id);
        lock (_gate) if (VisibleState() is { } state) store.Save(state);
        Notify();
    }
    public void DiscardPending(string? expectedId = null)
    {
        BackgroundOperationExecution? job; lock (_gate) job = expectedId is null ? _selected : _jobs.GetValueOrDefault(expectedId);
        job?.DiscardPending(expectedId);
    }
    public bool FailWaiting(string expectedId, string reason)
    { BackgroundOperationExecution? job; lock (_gate) job = _jobs.GetValueOrDefault(expectedId); return job?.FailWaiting(expectedId, reason) == true; }
}
