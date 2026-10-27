using System.IO;
using AIHub.Models;

namespace AIHub.Services.VideoPreparation;

public sealed record VideoConceptChoice(string Id, string Name, DebugModelInfo? Model, bool? Fits, string Placement);

/// <summary>The user-approved subset of the existing catalog, using current memory and the runtime's own reserves.</summary>
public static class VideoConceptModels
{
    public static readonly string[] Ids = ["executor", "profile-katarau", "core"];
    public const int Context = 8192;
    public static string Name(string id) => id switch
    { "executor" => "Qwen3.6-27B Q4_K_M", "profile-katarau" => "Katarau 9B Q4_K_M", "core" => "Qwen3-8B Q4_K_M", _ => throw new InvalidDataException("VideoPrep.AiModelMissing") };

    public static string Default(IReadOnlyList<VideoConceptChoice> choices) =>
        Ids.FirstOrDefault(id => choices.Any(c => c.Id == id && c.Fits != false)) ?? "core";

    public static async Task<IReadOnlyList<VideoConceptChoice>> DiscoverAsync(StorageSettings storage, CancellationToken token)
    {
        var installed = await Task.Run(() => MusicPoetryModelCatalog.Choices(storage), token);
        RuntimeDevice? gpu = null;
        var gpuKnown = false;
        if (ApplicationBackgroundOperations.Current?.BlocksNewWork != true)
        {
            try
            {
                var selection = await LlamaRuntimeSelector.SelectAsync(0, false, _ => { }, token);
                if (selection.Device is { } device)
                {
                    // Shared/integrated memory is not added to physical RAM as if it were a second pool.
                    using var monitor = new GpuAdapterMemoryMonitor(device); gpu = monitor.Sample(); gpuKnown = true;
                }
                else gpuKnown = true;
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or System.Runtime.InteropServices.COMException)
            { OwnedProcessRegistry.Log("concept_hardware_estimate_unavailable", "VideoPreparation", detail: error.GetType().Name); }
        }
        return await Task.Run(() => Ids.Select(id =>
        {
            var model = installed.FirstOrDefault(c => c.Candidate.Id == id)?.Model;
            if (model is null) return new VideoConceptChoice(id, Name(id), null, null, "AiNeedsPreparation");
            try
            {
                var memory = LiteraryModelMemoryMetadata.Read(model.Path);
                var context = Math.Min(Context, memory.ModelContextTokens);
                var kv = LlamaDenseMemoryPolicy.KvBytes(memory, context);
                var cpu = LiteraryRamReservePolicy.EvaluateCurrent(checked(memory.FileBytes + kv));
                var requiredGpu = LlamaDenseMemoryPolicy.GpuRequired(memory, context);
                if (gpu is not null && requiredGpu <= gpu.FreeBytes - gpu.TotalBytes / 33
                    && LiteraryRamReservePolicy.EvaluateCurrent(2 * LlamaDenseMemoryPolicy.GiB).Allowed)
                    return new VideoConceptChoice(id, Name(id), model, true, "AiGpuEstimate");
                if (cpu.Allowed)
                {
                    if (gpu is not null)
                    {
                        try
                        {
                            _ = LlamaLayerPlacement.Read(model.Path, memory).HybridLayers(gpu, kv);
                            return new VideoConceptChoice(id, Name(id), model, true, "AiHybridEstimate");
                        }
                        catch (IOException) { }
                    }
                    return new VideoConceptChoice(id, Name(id), model, true, "AiCpuEstimate");
                }
                return new VideoConceptChoice(id, Name(id), model, gpuKnown ? false : null, "AiMemoryUnavailable");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or OverflowException or KeyNotFoundException)
            { return new VideoConceptChoice(id, Name(id), model, null, "AiHardwareUnknown"); }
        }).ToArray(), token);
    }
}
