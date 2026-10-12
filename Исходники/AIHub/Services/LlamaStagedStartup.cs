using System.IO;

namespace AIHub.Services;

internal enum LlamaPlacementMode { Gpu, Hybrid, Cpu }
internal sealed record LlamaStartupPlacement(LlamaPlacementMode Mode, int GpuLayers, int ContextTokens);
internal sealed class LlamaVramPressureException : IOException
{
    public LlamaVramPressureException() : base("The selected GPU reached the 97 percent resident or allocation budget during loading.") { }
}

/// <summary>One-way startup transitions. Retirement must finish before any successor starts.</summary>
internal static class LlamaStagedStartup
{
    internal static bool AtVramLimit(RuntimeDevice device) => device.TotalBytes > 0 && device.FreeBytes >= 0
        && device.FreeBytes <= device.TotalBytes && (decimal)device.FreeBytes * 100 <= (decimal)device.TotalBytes * 3;

    internal static async Task<LlamaStartupPlacement> RunAsync(LlamaStartupPlacement initial,
        Func<LlamaStartupPlacement, CancellationToken, Task<LlamaStartupPlacement>> load,
        Func<CancellationToken, Task<LlamaStartupPlacement>> hybrid,
        Func<Task> retire, Action<string> log, CancellationToken token)
    {
        var next = initial;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return await load(next, token); }
            catch (OperationCanceledException) { await retire(); throw; }
            catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
            {
                log($"placement_failure: mode={next.Mode}; layers={next.GpuLayers}; error={error}");
                await retire(); token.ThrowIfCancellationRequested();
                if (next.Mode == LlamaPlacementMode.Cpu) throw;
                if (next.Mode == LlamaPlacementMode.Gpu)
                {
                    try { next = await hybrid(token); }
                    catch (Exception planningError) when (planningError is IOException or InvalidOperationException or TimeoutException)
                    {
                        log($"hybrid_plan_failure: {planningError}");
                        next = new(LlamaPlacementMode.Cpu, 0, next.ContextTokens);
                    }
                }
                else next = new(LlamaPlacementMode.Cpu, 0, next.ContextTokens);
            }
        }
    }
}
