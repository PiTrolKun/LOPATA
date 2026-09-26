using System.IO;

namespace AIHub.Services;

public sealed partial class LiteraryChatRuntime
{
    private LiteraryRuntimeOptions _runtimeOptions = new();
    private LiteraryModelMemoryMetadata? _modelMemoryMetadata;
    private bool _loadedUsesRamReserve;
    private readonly object _retirementGate = new();
    private Task _processRetirement = Task.CompletedTask;
    public int? ModelContextTokens => _modelMemoryMetadata?.ModelContextTokens;
    internal bool UsesRamReserve => _runtimeOptions.UseRamReserve;

    internal void BeginBudgetOperation(LiteraryRuntimeOptions? options = null)
    {
        _resourcesChecked = false; _resizeAttempted = false;
        _runtimeOptions = options ?? new();
        // A retry must measure memory after releasing the old server's allocations.
        if (_runtimeOptions.RefreshMemory || _runtimeOptions.UseRamReserve) StopProcess();
    }

    internal void EndBudgetOperation()
    {
        var wasReserve = _runtimeOptions.UseRamReserve;
        _runtimeOptions = new();
        if (!wasReserve) return;
        // Restore the ordinary profile lazily; never let cleanup retain the queue lock.
        try { StopProcess(); Log("Temporary RAM reserve released; next request uses the ordinary GPU profile."); }
        catch (Exception ex)
        { Log("Temporary RAM reserve cleanup failed: " + ex.Message); }
    }

    public ImageAnalysisContextExhaustedException CreateContextExhaustedException(int input,
        int minimumReply = LiteraryAutomaticBudget.MinimumReply)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(input);
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumReply, LiteraryAutomaticBudget.MinimumReply);
        return new("The loaded context has insufficient space for this request.", budget:
            LiteraryRamReservePolicy.Snapshot(_modelMemoryMetadata, ContextCapacity, input, minimumReply, _runtimeOptions.UseRamReserve));
    }

    private async Task<int> PreparePlacementAsync(string model, LiteraryStartupDiagnostics trace, CancellationToken ct)
    {
        _modelMemoryMetadata = await Task.Run(() => LiteraryModelMemoryMetadata.Read(model), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        trace.Record("model_memory_metadata", _modelMemoryMetadata);
        if (!_runtimeOptions.UseRamReserve) return 99;
        if (!_modelMemoryMetadata.SupportsRamReserve)
            throw new InvalidDataException("The selected model has no verified RAM reserve profile.");
        var memory = LiteraryRamReservePolicy.EvaluateCurrent(_modelMemoryMetadata.FileBytes);
        trace.Record("ram_reserve_budget", memory);
        if (!memory.Allowed) throw new LiteraryRamReserveException(memory.RequiredBytes, memory.AvailableBytes);
        return _modelMemoryMetadata.ReserveGpuLayers;
    }

    private async Task AwaitProcessRetirementAsync(CancellationToken ct)
    {
        Task pending;
        lock (_retirementGate) pending = _processRetirement;
        try { await pending.WaitAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false); }
        catch (TimeoutException ex) { throw new IOException("The previous literary runtime has not released its process yet.", ex); }
    }

    private static async Task RetireProcessAsync(System.Diagnostics.Process process)
    {
        try { await process.WaitForExitAsync().ConfigureAwait(false); }
        catch (InvalidOperationException) { } // A cancelled startup may never have started.
        finally { process.Dispose(); }
    }
}
