using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

public sealed partial class LlamaServerRuntimeService
{
    private readonly LlamaPlacementPreferences _placementPreferences;
    private LlamaGpuAllocationTracker? _poetryAllocation;
    private async Task StartPoetryPlacementAsync(DebugModelInfo model, LiteraryModelMemoryMetadata memory,
        Action<string> log, CancellationToken token, bool recover)
    {
        var journal = _poetryJournal ?? throw new InvalidOperationException("Startup journal was not initialized.");
        var startupTimeout = _poetryStartupTimeout;
        // Ask for a compatible device, not proof that an upper-bound full allocation fits.
        var gpu = await LlamaRuntimeSelector.SelectAsync(0, _forceCpu, log, token);
        GpuAdapterMemoryMonitor? monitor = null;
        try { if (gpu.Device is { } device) monitor = new(device); }
        catch (Exception error) when (error is IOException or System.Runtime.InteropServices.COMException)
        { journal.Record("vram_monitor_unavailable", error.ToString()); }
        using var monitorLifetime = monitor;
        var requested = Math.Min(_poetryRequestedContextTokens ?? CoreContextRuntimeLimits.CurrentBackendContextLimit, memory.ModelContextTokens);
        var key = LlamaPlacementPreferences.Key(model.Path, requested, gpu);
        var preferences = _placementPreferences;
        LlamaStartupPlacement? saved = null;
        try { saved = preferences.Load(key, memory.BlockCount, requested); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { journal.Record("preference_unavailable", error.Message); }
        var initial = saved ?? new(gpu.UsesGpu ? LlamaPlacementMode.Gpu : LlamaPlacementMode.Cpu,
            gpu.UsesGpu ? memory.BlockCount + 1 : 0, requested);
        if (recover && gpu.UsesGpu)
        {
            if (_poetryPlacement?.Mode == LlamaPlacementMode.Gpu)
            {
                try { initial = await Hybrid(token); }
                catch (IOException error)
                { journal.Record("hybrid_plan_failure", error.ToString()); initial = new(LlamaPlacementMode.Cpu, 0, RequestedContextTokens); }
            }
            else initial = new(LlamaPlacementMode.Cpu, 0, RequestedContextTokens);
        }
        journal.Record("preference", new { key, reused = saved is not null && !recover, initial });
        var successful = await LlamaStagedStartup.RunAsync(initial, Load, Hybrid, RetirePoetryForRecoveryAsync,
            text => journal.Record("transition", text), token);
        _poetryPlacement = successful;
        try { preferences.Save(key, successful); journal.Record("preference_saved", successful); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { journal.Record("preference_write_failed", error.ToString()); }

        async Task<LlamaStartupPlacement> Load(LlamaStartupPlacement placement, CancellationToken cancellation)
        {
            _poetryContextTokens = placement.ContextTokens;
            var selected = gpu;
            if (placement.Mode == LlamaPlacementMode.Cpu)
            {
                selected = await LlamaRuntimeSelector.SelectAsync(0, true, log, cancellation);
                while (true)
                {
                    try { LlamaDenseMemoryPolicy.EnsureCurrentCpuMemory(memory, RequestedContextTokens); break; }
                    catch (IOException) when (_poetryCanResize && RequestedContextTokens > 1024)
                    {
                        _poetryContextTokens = Math.Max(1024, RequestedContextTokens / 2 / 256 * 256);
                        journal.Record("cpu_context_reduced", RequestedContextTokens);
                    }
                }
            }
            else
            {
                if (!gpu.UsesGpu) throw new IOException("The remembered GPU is unavailable.");
                if (placement.Mode == LlamaPlacementMode.Hybrid)
                    LlamaDenseMemoryPolicy.EnsureCurrentCpuMemory(memory, RequestedContextTokens);
                selected = gpu with { GpuLayerOverride = placement.GpuLayers };
            }
            journal.Record("loading", new { placement, context = RequestedContextTokens,
                kvBytes = LlamaDenseMemoryPolicy.KvBytes(memory, RequestedContextTokens) });
            var baseline = selected.UsesGpu ? await CurrentDevice(cancellation) : null;
            _poetryAllocation = baseline is null ? null : new(selected.DeviceId, baseline.TotalBytes - baseline.FreeBytes);
            StartProcess(model, selected, log);
            await WaitForHealthAsync(log, cancellation, selected.UsesGpu ? Sample : null, startupTimeout);
            var result = placement with { ContextTokens = RequestedContextTokens };
            journal.Record("ready", result);
            return result;
        }

        async Task<LlamaStartupPlacement> Hybrid(CancellationToken cancellation)
        {
            LlamaDenseMemoryPolicy.EnsureCurrentCpuMemory(memory, RequestedContextTokens);
            await Task.Delay(250, cancellation); // Let driver accounting catch up after the retired worker.
            var device = await CurrentDevice(cancellation);
            var layout = await Task.Run(() => LlamaLayerPlacement.Read(model.Path, memory), cancellation);
            var layers = layout.HybridLayers(device, LlamaDenseMemoryPolicy.KvBytes(memory, RequestedContextTokens));
            journal.Record("hybrid_plan", new { layers, device, layout.FixedBytes, layout.LayerBytes });
            return new(LlamaPlacementMode.Hybrid, layers, RequestedContextTokens);
        }

        Task<RuntimeDevice> CurrentDevice(CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            return Task.FromResult(monitor?.Sample() ?? throw new IOException("Selected GPU VRAM monitoring is unavailable."));
        }
        async Task Sample(CancellationToken cancellation)
        {
            var device = await CurrentDevice(cancellation);
            var intended = _poetryAllocation?.IntendedUsedBytes ?? 0;
            var effective = device with { FreeBytes = Math.Min(device.FreeBytes, Math.Max(0, device.TotalBytes - intended)) };
            journal.Record("vram", new { device, intendedUsedBytes = intended, effective });
            if (LlamaStagedStartup.AtVramLimit(effective)) throw new LlamaVramPressureException();
        }
    }
}
