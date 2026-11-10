using System.IO;
using AIHub.Services.VideoEditor;

namespace AIHub.Services.SpeechAssets;

/// <summary>Scenario-owned narrator. A compute lease is held only while preparing or synthesizing.</summary>
internal sealed class SpeechAssetModelSession
{
    private readonly SemaphoreSlim _gate = new(1);
    private readonly object _state = new();
    private readonly VideoNarratorBatch _batch;
    private readonly string _directory;
    private CancellationTokenSource? _current;
    private VideoNarratorChoice? _warm;
    internal SpeechAssetModelSession(VideoNarratorRegistry? registry = null, string? directory = null)
    {
        _batch = new(registry ?? new());
        _directory = directory ?? Path.Combine(AppDataPaths.BaseDirectory, "SpeechAssets", "Warmup", Guid.NewGuid().ToString("N"));
        ApplicationBackgroundOperations.RegisterModel(this, model => model.RetireAsync());
    }
    internal bool IsWarm(VideoNarratorChoice choice)
    { lock (_state) return _warm == choice && _batch.IsResident; }

    internal async Task<Lease> AcquireAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, ApplicationBackgroundOperations.ExitToken);
            lock (_state) _current = cancellation;
            cancellation.Token.ThrowIfCancellationRequested();
            return new(this, cancellation);
        }
        catch { lock (_state) { _current?.Dispose(); _current = null; } _gate.Release(); throw; }
    }

    internal async Task WarmAsync(VideoNarratorChoice choice, CancellationToken token)
    {
        using var lease = await AcquireAsync(token);
        if (IsWarm(choice)) return;
        lock (_state) _warm = null;
        using var audit = ComponentAudit.CaptureOperation();
        try
        {
            // The existing audited narrator performs a small real inference, loading weights and kernels.
            // This private probe is never inserted into a project or offered as an approved take.
            Lopata.Updates.SafeUpdatePath.RejectLinks(_directory);
            Directory.CreateDirectory(_directory);
            using var work = VideoNarrationWorkspace.Capture(_directory, publishOutput: false);
            var narrator = lease.Batch.Get(choice.Id);
            var text = ChatSpeechText.TestPhrase(choice.Language);
            if (narrator is IConfiguredVideoNarrator configured)
                await configured.GenerateAsync(choice.Id, choice.Language, text, choice.Rate, choice.Options, lease.Token);
            else await narrator.GenerateAsync(choice.Id, choice.Language, text, choice.Rate, lease.Token);
            lease.Token.ThrowIfCancellationRequested(); lease.MarkWarm(choice);
        }
        catch { lease.Reset(); throw; }
    }

    internal async Task RetireAsync()
    {
        lock (_state) _current?.Cancel();
        await _gate.WaitAsync();
        try { lock (_state) _warm = null; await Task.Run(() => _batch.Dispose()); }
        finally { _gate.Release(); }
    }

    internal sealed class Lease(SpeechAssetModelSession owner, CancellationTokenSource cancellation) : IDisposable
    {
        internal VideoNarratorBatch Batch => owner._batch;
        internal CancellationToken Token => cancellation.Token;
        internal void MarkWarm(VideoNarratorChoice choice) { lock (owner._state) owner._warm = choice; }
        internal void Reset() { lock (owner._state) owner._warm = null; owner._batch.Dispose(); }
        public void Dispose()
        {
            lock (owner._state) { if (ReferenceEquals(owner._current, cancellation)) owner._current = null; }
            cancellation.Dispose(); owner._gate.Release();
        }
    }
}
