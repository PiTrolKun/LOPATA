using System.Diagnostics;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>One logical operation; adapters own its checkpoints, replay and model retirement.</summary>
public sealed class BackgroundOperationController(BackgroundOperationStore store)
{
    private readonly object _gate = new();
    private readonly AsyncLocal<string?> _scope = new();
    private readonly Dictionary<string, Func<BackgroundOperationState, CancellationToken, Task>> _restorers = [];
    private CancellationTokenSource? _attempt;
    private TaskCompletionSource? _paused, _resume;
    private bool _pauseRequested, _running;
    private bool _auxiliaryRunning;
    private bool _loadFailed;
    private TaskCompletionSource _idle = CompletedSource();
    private BackgroundOperationState? _state;
    private readonly Stopwatch _elapsed = new();
    private double _previousElapsed;
    public BackgroundOperationState? State { get { lock (_gate) return _state; } }
    public bool IsRunning { get { lock (_gate) return _running || _auxiliaryRunning; } }
    internal bool IsInOperationScope { get { lock (_gate) return _running && _scope.Value is not null && _scope.Value == _state?.Id; } }
    public bool HasPending { get { lock (_gate) return _state is not null && !IsFinal(_state.Phase); } }
    public bool CanRestore { get { lock (_gate) return _state is not null && _restorers.ContainsKey(_state.Kind); } }
    public event Action? Changed;
    public event Action<BackgroundOperationNotice>? Completed;
    public BackgroundOperationState? LoadResult(string id) => store.LoadResult(id);
    private static TaskCompletionSource CompletedSource()
    { var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); source.SetResult(); return source; }
    public Task WaitForIdleAsync() { lock (_gate) return _idle.Task; }

    /// <summary>Non-durable auxiliary windows still share the application model exclusion gate.</summary>
    public IDisposable BeginAuxiliary()
    {
        lock (_gate)
        {
            if (_running || _auxiliaryRunning || _loadFailed || _state is not null && !IsFinal(_state.Phase))
                throw new InvalidOperationException("Music.Poetry.OtherOperation");
            _auxiliaryRunning = true; _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        NotifyChanged(); return new AuxiliaryLease(this);
    }
    private sealed class AuxiliaryLease(BackgroundOperationController owner) : IDisposable
    {
        private BackgroundOperationController? _owner = owner;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is not { } controller) return;
            lock (controller._gate) { controller._auxiliaryRunning = false; controller._idle.TrySetResult(); }
            controller.NotifyChanged();
        }
    }

    public void CheckpointForExit()
    {
        lock (_gate)
        {
            if (_state is null || IsFinal(_state.Phase)) return;
            Persist(_state with { Phase = BackgroundOperationPhase.Waiting,
                UserPaused = _state.UserPaused || _pauseRequested, ElapsedSeconds = _running ? Elapsed : _state.ElapsedSeconds });
        }
        NotifyChanged();
    }

    public void Register(string kind, Func<BackgroundOperationState, CancellationToken, Task> restore)
    {
        lock (_gate) _restorers.Add(kind, restore);
    }

    public void Load()
    {
        lock (_gate)
        {
            if (_running || _auxiliaryRunning) throw new InvalidOperationException("Cannot load over a running operation.");
            try { _state = store.Load(); _loadFailed = false; }
            catch { _loadFailed = true; throw; }
            if (_state is { NeedsAttention: true, Notice: not null, Notices.Count: 0 })
                Persist(_state with { Notices = [_state.Notice] });
            if (_state is { Phase: BackgroundOperationPhase.Running or BackgroundOperationPhase.Pausing or BackgroundOperationPhase.Countdown })
                Persist(_state with { Phase = BackgroundOperationPhase.Waiting, Detail = null });
        }
        NotifyChanged();
    }

    public void SaveCheckpoint<T>(T checkpoint)
    {
        lock (_gate)
        {
            if (!_running || _state is null || _scope.Value != _state.Id) throw new InvalidOperationException("No matching operation scope.");
            Persist(_state with { Checkpoint = JsonSerializer.SerializeToElement(checkpoint), ElapsedSeconds = Elapsed });
        }
    }

    public async Task<T> RunAsync<T>(BackgroundOperationState input, Func<CancellationToken, Task<T>> execute,
        Func<Task> releaseModels, CancellationToken lifetime)
    {
        if (IsInOperationScope) return await execute(lifetime);
        lock (_gate)
        {
            if (_running || _auxiliaryRunning) throw new InvalidOperationException("Another model operation is active.");
            if (_loadFailed) throw new InvalidOperationException("A damaged background checkpoint requires review before starting a new operation.");
            if (_state is { Phase: not (BackgroundOperationPhase.Completed or BackgroundOperationPhase.Failed or BackgroundOperationPhase.Canceled) }
                && _state.Id != input.Id) throw new InvalidOperationException("Resume or discard the pending operation first.");
            Persist(input with { Phase = BackgroundOperationPhase.Running, UserPaused = false, RequiresDecision = false, Detail = null,
                Notice = _state?.Notice ?? input.Notice, Notices = _state?.Notices ?? input.Notices,
                NeedsAttention = _state?.NeedsAttention ?? input.NeedsAttention });
            _running = true; _pauseRequested = false;
            _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _previousElapsed = input.ElapsedSeconds; _elapsed.Restart();
        }
        _scope.Value = input.Id;
        NotifyChanged();
        try
        {
            while (true)
            {
                lifetime.ThrowIfCancellationRequested();
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                bool cancelAttempt;
                lock (_gate) { _attempt = attempt; cancelAttempt = _pauseRequested; }
                if (cancelAttempt) attempt.Cancel();
                T result = default!;
                try { result = await execute(attempt.Token); }
                catch (OperationCanceledException) when (!lifetime.IsCancellationRequested && PauseRequested) { }
                BackgroundOperationNotice? notice = null;
                bool finished;
                lock (_gate)
                {
                    // Completion and pause compete under the same lock; no orphaned pause waiter.
                    finished = !_pauseRequested;
                    if (finished)
                    {
                        var seconds = Elapsed;
                        if (seconds > 30) notice = new(_state!.Id, _state.Kind, _state.Title, _state.Project, _state.Private);
                        var notices = notice is null ? _state!.Notices : _state!.Notices.Where(n => n.Id != notice.Id).Append(notice).ToArray();
                        Persist(_state! with { Phase = BackgroundOperationPhase.Completed, NeedsAttention = notice is not null || _state!.NeedsAttention,
                            Notice = notice ?? _state!.Notice, Notices = notices,
                            ElapsedSeconds = seconds, UserPaused = false });
                        _attempt = null;
                    }
                }
                if (finished)
                {
                    if (notice is not null) NotifyCompleted(notice);
                    return result;
                }
                lifetime.ThrowIfCancellationRequested();
                _elapsed.Stop();
                // The canceled stage has unwound before its model processes are retired.
                await releaseModels();
                Task resume;
                lock (_gate)
                {
                    Persist(_state! with { Phase = BackgroundOperationPhase.Paused, UserPaused = true, ElapsedSeconds = Elapsed });
                    _attempt = null; _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    resume = _resume.Task; _paused?.TrySetResult();
                }
                NotifyChanged();
                await resume.WaitAsync(lifetime);
                lock (_gate)
                {
                    _pauseRequested = false; _resume = null; _paused = null;
                    Persist(_state! with { Phase = BackgroundOperationPhase.Running, UserPaused = false });
                }
                _elapsed.Start(); NotifyChanged();
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            await releaseModels();
            lock (_gate) Persist(_state! with { Phase = BackgroundOperationPhase.Waiting,
                UserPaused = _state!.UserPaused || _pauseRequested, ElapsedSeconds = Elapsed });
            throw;
        }
        catch (Exception error)
        {
            lock (_gate)
            {
                try { Persist(_state! with { Phase = BackgroundOperationPhase.Waiting, RequiresDecision = true,
                    Detail = error is BackgroundOperationWaitingException ? error.Message : error.GetType().Name, ElapsedSeconds = Elapsed }); }
                catch (Exception saveError) { OwnedProcessRegistry.Log("background_checkpoint_failed", "Application", detail: saveError.GetType().Name); }
                _paused?.TrySetException(error);
            }
            throw;
        }
        finally
        {
            lock (_gate) { _attempt = null; _running = false; _elapsed.Stop(); _idle.TrySetResult(); }
            _scope.Value = null; NotifyChanged();
        }
    }

    private bool PauseRequested { get { lock (_gate) return _pauseRequested; } }
    private double Elapsed => _previousElapsed + _elapsed.Elapsed.TotalSeconds;
    private static bool IsFinal(BackgroundOperationPhase phase) => phase is BackgroundOperationPhase.Completed or BackgroundOperationPhase.Failed or BackgroundOperationPhase.Canceled;
    private void NotifyChanged()
    {
        foreach (Action listener in Changed?.GetInvocationList() ?? [])
            try { listener(); } catch (Exception error) { OwnedProcessRegistry.Log("background_observer_failed", "Application", detail: error.GetType().Name); }
    }
    private void NotifyCompleted(BackgroundOperationNotice notice)
    {
        foreach (Action<BackgroundOperationNotice> listener in Completed?.GetInvocationList() ?? [])
            try { listener(notice); } catch (Exception error) { OwnedProcessRegistry.Log("background_observer_failed", "Application", detail: error.GetType().Name); }
    }
    private void Persist(BackgroundOperationState state)
    {
        var next = state with { UpdatedUtc = DateTimeOffset.UtcNow };
        store.Save(next); _state = next;
    }

    public async Task PauseAsync()
    {
        Task paused;
        CancellationTokenSource? attempt = null;
        lock (_gate)
        {
            if (_state is null || IsFinal(_state.Phase)) return;
            if (!_running)
            {
                if (_state is not null) Persist(_state with { UserPaused = true, Phase = BackgroundOperationPhase.Paused });
                paused = Task.CompletedTask;
            }
            else if (_state?.Phase == BackgroundOperationPhase.Paused) return;
            else
            {
                Persist(_state! with { Phase = BackgroundOperationPhase.Pausing, UserPaused = true });
                _pauseRequested = true;
                _paused ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
                paused = _paused.Task; attempt = _attempt;
            }
        }
        // Cancellation callbacks can checkpoint on another thread. Never invoke them while
        // holding the state lock; completion may have disposed this attempt in the meantime.
        try { attempt?.Cancel(); } catch (ObjectDisposedException) { }
        catch (AggregateException error) { OwnedProcessRegistry.Log("background_cancel_callback_failed", "Application", detail: error.GetType().Name); }
        NotifyChanged(); await paused;
    }

    public async Task ResumeAsync(CancellationToken token)
    {
        Func<BackgroundOperationState, CancellationToken, Task>? restore;
        BackgroundOperationState? state;
        lock (_gate)
        {
            if (_resume is not null) { _resume.TrySetResult(); return; }
            state = _state;
            if (state is null || _running || IsFinal(state.Phase)) return;
            _restorers.TryGetValue(state.Kind, out restore);
            if (restore is null) { Persist(state with { Phase = BackgroundOperationPhase.Waiting, RequiresDecision = true, Detail = "adapter_required" }); }
        }
        NotifyChanged();
        if (restore is not null)
        {
            try { await restore(state!, token); }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                lock (_gate)
                    if (_state?.Id == state!.Id && !_running && !IsFinal(_state.Phase))
                        Persist(_state with { Phase = BackgroundOperationPhase.Waiting, RequiresDecision = true, Detail = error.GetType().Name });
                NotifyChanged(); throw;
            }
        }
    }

    public void SetCountdown(bool enabled)
    {
        lock (_gate)
        {
            if (_running || _state is null || _state.UserPaused
                || IsFinal(_state.Phase)) return;
            Persist(_state with { Phase = enabled ? BackgroundOperationPhase.Countdown : BackgroundOperationPhase.Waiting });
        }
        NotifyChanged();
    }

    public void Acknowledge(string id)
    {
        lock (_gate)
        {
            if (_state is { NeedsAttention: true } && _state.Notices.Any(n => n.Id == id))
            {
                var remaining = _state.Notices.Where(n => n.Id != id).ToArray();
                Persist(_state with { NeedsAttention = remaining.Length > 0, Notice = remaining.LastOrDefault(), Notices = remaining });
            }
        }
        NotifyChanged();
    }

    public void DiscardPending(string? expectedId = null)
    {
        lock (_gate)
        {
            if (_running) throw new InvalidOperationException("Cancel the active operation before discarding it.");
            if (expectedId is not null && _state?.Id != expectedId) return;
            if (_state is not null && !IsFinal(_state.Phase)) Persist(_state with { Phase = BackgroundOperationPhase.Canceled, UserPaused = false });
        }
        NotifyChanged();
    }
}
