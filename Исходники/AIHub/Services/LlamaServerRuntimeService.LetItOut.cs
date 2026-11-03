using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using AIHub.Models;

namespace AIHub.Services;

public sealed partial class LlamaServerRuntimeService
{
    private bool _letItOutEnabled;
    private string? _letItOutProjector;
    internal async Task SetLetItOutProjectorAsync(string? path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_letItOutProjector == path) return;
        await RetireLetItOutAsync(); token.ThrowIfCancellationRequested(); _letItOutProjector = path;
    }
    private int _letItOutCpuExperts;
    private int _letItOutContextTokens = LetItOutChatRuntime.ContextTokens;
    internal int LetItOutContextTokens => _letItOutContextTokens;
    private GpuAdapterMemoryMonitor? _letItOutMemory;
    private Func<LlamaGpuAdmission, CancellationToken, Task<LlamaGpuDecision>>? _letItOutDecision;
    private Action<string>? _letItOutMode;
    // An in-process integration harness can project measured usage onto a smaller adapter budget.
    // The desktop never sets this; there is no environment override or user mode preference.
    internal Func<RuntimeDevice, RuntimeDevice>? LetItOutMemoryProjection { get; set; }

    internal void ConfigureLetItOut(Func<LlamaGpuAdmission, CancellationToken, Task<LlamaGpuDecision>>? decide,
        Action<string>? mode)
    {
        _letItOutEnabled = true; _letItOutDecision = decide; _letItOutMode = mode;
    }

    private RuntimeDevice ProjectLetItOutMemory(RuntimeDevice measured) => LetItOutMemoryProjection?.Invoke(measured) ?? measured;

    private RuntimeDevice? MeasureLetItOutDevice(RuntimeDevice device)
    {
        try { using var monitor = new GpuAdapterMemoryMonitor(device); return ProjectLetItOutMemory(monitor.Sample()); }
        catch (Exception error) when (error is IOException or COMException or System.ComponentModel.Win32Exception)
        { return null; } // Unknown adapter-wide usage is not evidence of free VRAM.
    }

    private async Task StartLetItOutPlacementAsync(DebugModelInfo model, LiteraryModelMemoryMetadata metadata,
        Action<string> log, CancellationToken token, bool forceCpu)
    {
        var setId = EngineSetSelection.ActiveId;
        if (setId != NativeRuntimeReleasePolicy.UpdatedSet)
            throw new LetItOutMemoryException("LetItOut.Preparation.EngineRequired");
        var policy = await Task.Run(() => LetItOutMemoryPolicy.Read(model.Path, metadata, LetItOutContextPolicy.Minimum), token);
        if (_letItOutProjector is not null) policy = policy with { ExtraHostBytes = 2 * new FileInfo(_letItOutProjector).Length + 512L * 1024 * 1024 };
        var preferredPolicy = policy with { KvBytes = LlamaDenseMemoryPolicy.KvBytes(metadata,
            Math.Min(LetItOutContextPolicy.Preferred, metadata.ModelContextTokens)) };
        var mayOffer = true; var minimumExperts = 0; var failedLoads = 0;
        forceCpu |= _forceCpu;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (EngineSetSelection.ActiveId != setId) throw new InvalidOperationException("EngineSets.Changed");
            var selection = await LlamaRuntimeSelector.SelectAsync(LlamaRuntimeSelector.InstalledBundles(setId),
                0, forceCpu, log, token, policy.FullGpuRequired, MeasureLetItOutDevice);
            if (mayOffer && preferredPolicy.ShouldOfferFreeMemory(selection.Device))
            {
                var device = selection.Device!;
                var choice = _letItOutDecision is null ? LlamaGpuDecision.Cancel
                    : await _letItOutDecision(new("gpu_memory_busy", device.Name,
                        preferredPolicy.FullGpuRequired, device.FreeBytes, device.TotalBytes), token);
                token.ThrowIfCancellationRequested();
                if (choice == LlamaGpuDecision.Cancel) throw new OperationCanceledException(token);
                if (choice == LlamaGpuDecision.Retry) continue;
                mayOffer = false;
                // Resample after a modal decision: other programs may have opened or closed.
                continue;
            }
            var (plan, context) = LetItOutContextPolicy.Place(metadata, policy, selection.Device,
                bytes => LiteraryRamReservePolicy.EvaluateCurrent(bytes).Allowed, minimumExperts);
            LetItOutMemoryPolicy.EnsureRam(plan);
            _letItOutContextTokens = context;
            if (plan.Mode == "Cpu") selection = await LlamaRuntimeSelector.SelectForSetAsync(setId, 0, true, log, token);
            _letItOutCpuExperts = plan.CpuMoeLayers;
            try
            {
                if (selection.Device is { } device) _letItOutMemory = new(device);
                StartProcess(model, selection, log);
                _letItOutMode?.Invoke("Loading" + plan.Mode);
                OwnedProcessRegistry.Log("let_it_out_placement", "LetItOutChat",
                    detail: $"mode={plan.Mode}; context={context}; cpuMoeLayers={plan.CpuMoeLayers}; gpuBudget={plan.GpuBytes}; reserve={LetItOutMemoryPolicy.VramReserve}; device={selection.DeviceId}");
                await WaitForHealthAsync(log, token, _letItOutMemory is null ? null : _ => {
                    CheckLetItOutReserve(); return Task.CompletedTask;
                });
                _letItOutMode?.Invoke(plan.Mode);
                return;
            }
            catch (Exception error) when (selection.UsesGpu && !token.IsCancellationRequested
                && (error is LetItOutMemoryException || error is IOException || error is COMException
                    || (error is InvalidOperationException or TimeoutException) && _hardwareStartupFailure))
            {
                // Placement can be retried before generation only; a user's answer is never silently regenerated.
                await RetireLetItOutAsync();
                failedLoads++;
                minimumExperts = plan.CpuMoeLayers + 1;
                forceCpu = failedLoads >= 2 || error is COMException || error is IOException and not LetItOutMemoryException;
                log($"Gemma placement changed before generation: {error.GetType().Name}.");
            }
        }
    }

    internal void AddLetItOutArguments(ProcessStartInfo start)
    {
        if (_letItOutProjector is not null)
            foreach (var value in new[] { "--mmproj", _letItOutProjector, "--no-mmproj-offload", "--image-max-tokens", "1024" }) AddArgument(start, value);
        foreach (var argument in new[] { "--fit", "off", "--no-context-shift", "--cache-ram", "0",
            "--ubatch-size", "512", "--cache-type-k", "f16", "--cache-type-v", "f16",
            "--reasoning-format", "deepseek", "--offline" }) AddArgument(start, argument);
        if (_letItOutCpuExperts > 0)
        {
            AddArgument(start, "--n-cpu-moe");
            AddArgument(start, _letItOutCpuExperts.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private void CheckLetItOutReserve()
    {
        if (_letItOutMemory is not null)
        {
            var memory=ProjectLetItOutMemory(_letItOutMemory.Sample());
            if(memory.FreeBytes<LetItOutMemoryPolicy.VramReserve)
            {
                OwnedProcessRegistry.Log("let_it_out_vram_low","LetItOutChat",detail:$"free={memory.FreeBytes}; total={memory.TotalBytes}; context={_letItOutContextTokens}");
                throw new LetItOutMemoryException("LetItOut.VramChanged");
            }
        }
    }

    private async Task<T> GuardLetItOutMemoryAsync<T>(Func<CancellationToken, Task<T>> run, CancellationToken token)
    {
        CheckLetItOutReserve();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        Exception? failure = null;
        async Task WatchAsync()
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    await Task.Delay(500, lifetime.Token);
                    CheckLetItOutReserve();
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception error) when (error is IOException or COMException)
            { failure = new LetItOutMemoryException("LetItOut.VramChanged"); await lifetime.CancelAsync(); }
        }
        var watch = _letItOutMemory is null ? Task.CompletedTask : WatchAsync();
        try
        {
            var result = await run(lifetime.Token);
            if (failure is not null) throw failure;
            CheckLetItOutReserve();
            return result;
        }
        catch (OperationCanceledException) when (failure is not null && !token.IsCancellationRequested) { throw failure; }
        finally { await lifetime.CancelAsync(); await watch; }
    }
}
