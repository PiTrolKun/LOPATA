using System.IO;

namespace AIHub.Services;

/// <summary>Conservative full-weight and F16 KV planning for ordinary dense attention.</summary>
internal static class LlamaDenseMemoryPolicy
{
    internal const long GiB = 1024L * 1024 * 1024;

    internal static long KvBytes(LiteraryModelMemoryMetadata model, int context)
    {
        if (context <= 0 || context > model.ModelContextTokens)
            throw new InvalidDataException("Requested context is outside the model's native capacity.");
        if (model.Architecture is not ("llama" or "qwen2" or "qwen3" or "qwen2moe" or "qwen3moe" or "qwen35" or "qwen35moe"
            or "phi2" or "phi3" or "gemma" or "gemma2" or "gemma3" or "starcoder2" or "gpt2"
            or "falcon" or "gptneox" or "stablelm" or "olmo" or "olmo2" or "deci")
            || model.BlockCount is <= 0 or > 4096
            || model.HeadCount <= 0 || model.KvHeadCount < 0 || model.KvHeadCount == 0 && model.Architecture != "deci" || model.EmbeddingLength <= 0
            || model.KvHeadCount > model.HeadCount
            || ((model.KeyLength <= 0 || model.ValueLength <= 0) && model.EmbeddingLength % model.HeadCount != 0))
            throw new InvalidDataException("This model requires a dedicated memory planner.");
        // A full-context allocation with the larger SWA head is an upper bound.
        // It does not depend on the backend enabling sliding-window cache savings.
        var key = Math.Max(model.KeyLengthSwa, model.KeyLength > 0 ? model.KeyLength : model.EmbeddingLength / model.HeadCount);
        var value = Math.Max(model.ValueLengthSwa, model.ValueLength > 0 ? model.ValueLength : model.EmbeddingLength / model.HeadCount);
        // b9442's default F16 cache, padded allocation; count every attention layer.
        var padded = checked(((long)context + 255) / 256 * 256);
        var layers = model.BlockCount;
        long recurrentBytes = 0;
        if (model.Architecture is "qwen35" or "qwen35moe")
        {
            if (model.HeadCounts is not null || model.KvHeadCounts is not null)
                throw new InvalidDataException("Per-layer hybrid attention requires a dedicated planner.");
            if (model.SsmConvKernel <= 0 || model.SsmInnerSize <= 0 || model.SsmStateSize <= 0 || model.SsmGroupCount <= 0)
                throw new InvalidDataException("Hybrid model recurrent-state dimensions are missing.");
            var interval = model.FullAttentionInterval > 0 ? model.FullAttentionInterval : 4;
            var mainLayers = model.BlockCount - model.PredictionLayers;
            layers = checked(mainLayers / interval + model.PredictionLayers);
            var recurrentLayers = mainLayers - mainLayers / interval;
            // b9442: one sequence, no rollback snapshots; R/S use F32.
            // Count full MTP attention layers conservatively even when prediction is disabled.
            var conv = checked((model.SsmConvKernel - 1L) * (model.SsmInnerSize + 2L * model.SsmGroupCount * model.SsmStateSize));
            var state = checked((long)model.SsmStateSize * model.SsmInnerSize);
            recurrentBytes = checked((conv + state) * recurrentLayers * 4);
        }
        long kvHeads = 0;
        if (model.HeadCounts is not null && model.HeadCounts.Count != model.BlockCount
            || model.KvHeadCounts is not null && model.KvHeadCounts.Count != model.BlockCount)
            throw new InvalidDataException("Invalid per-layer attention dimension count.");
        for (var layer = 0; layer < layers; layer++)
        {
            var heads = model.HeadCounts?[layer] ?? model.HeadCount;
            var cached = model.KvHeadCounts?[layer] ?? model.KvHeadCount;
            if (heads < 0 || cached < 0 || cached > heads || heads > 1_000_000
                || model.Architecture != "deci" && (heads == 0 || cached == 0))
                throw new InvalidDataException("Invalid per-layer attention dimensions.");
            kvHeads = checked(kvHeads + cached);
        }
        return checked(padded * kvHeads * (key + (long)value) * 2 + recurrentBytes);
    }

    internal static long GpuRequired(LiteraryModelMemoryMetadata model, int context) =>
        checked(model.FileBytes + KvBytes(model, context) + 2 * GiB);

    internal static LiteraryRamReservePolicy.Decision CpuDecision(LiteraryModelMemoryMetadata model,
        int context, long total, long available) =>
        LiteraryRamReservePolicy.Evaluate(checked(model.FileBytes + KvBytes(model, context)), total, available);

    internal static void EnsureCurrentCpuMemory(LiteraryModelMemoryMetadata model, int context)
    {
        var decision = LiteraryRamReservePolicy.EvaluateCurrent(checked(model.FileBytes + KvBytes(model, context)));
        if (!decision.Allowed)
            throw new IOException($"Insufficient physical RAM for model and context: required={decision.RequiredBytes}; available={decision.AvailableBytes}.");
    }

    internal static int MaximumCpuContext(LiteraryModelMemoryMetadata model)
    {
        var minimum = 1024;
        EnsureCurrentCpuMemory(model, minimum);
        var low = minimum; var high = model.ModelContextTokens;
        while (low < high)
        {
            var middle = low + (high - low + 1) / 2;
            var decision = LiteraryRamReservePolicy.EvaluateCurrent(checked(model.FileBytes + KvBytes(model, middle)));
            if (decision.Allowed) low = middle; else high = middle - 1;
        }
        return low / 256 * 256;
    }
}
