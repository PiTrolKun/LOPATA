using System.IO;

namespace AIHub.Services.Books.Smart;

/// <summary>Dedicated full-context planner for the catalog-pinned official PaddleOCR-VL 1.6 BF16 pair.</summary>
internal static class BookSmartPaddleMemoryPolicy
{
    // The catalog verifies SHA256. These exact dimensions reject accidentally routed future architectures.
    internal const long ModelBytes = 935769056;
    internal const long ProjectorBytes = 881770560;
    internal static OmniRuntimeMemoryOverride Runtime { get; } = new(GpuRequired, EnsureCurrentCpuMemory);

    internal static long KvBytes(LiteraryModelMemoryMetadata model, int context)
    {
        Validate(model, ProjectorBytes);
        if (context <= 0 || context > model.ModelContextTokens)
            throw new InvalidDataException("Paddle OCR context exceeds the pinned model capacity.");
        // llama.cpp d4c8e2c29 src/models/paddleocr.cpp uses build_attn_inp_kv and all 18 attention layers.
        // Explicit GGUF K/V head dimensions are 128, not embedding_length / head_count (=64).
        // One sequence; both K and V default to F16 (2 bytes), context padded to 256 tokens.
        var padded = checked(((long)context + 255) / 256 * 256);
        return checked(padded * model.BlockCount * model.KvHeadCount * (model.KeyLength + (long)model.ValueLength) * 2);
    }

    internal static long GpuRequired(LiteraryModelMemoryMetadata model, long projectorBytes)
    {
        Validate(model, projectorBytes);
        // Full weights/projector + full native-context KV + separate 2GiB language/2GiB vision graph allowances.
        // These are conservative planning allowances, not a claim that the native process uses exactly this much VRAM.
        return checked(model.FileBytes + projectorBytes + KvBytes(model, OmniLlamaProtocol.ContextTokens) + 4 * LlamaDenseMemoryPolicy.GiB);
    }

    internal static LiteraryRamReservePolicy.Decision CpuDecision(LiteraryModelMemoryMetadata model, long projectorBytes,
        long total, long available) => LiteraryRamReservePolicy.Evaluate(GpuRequired(model, projectorBytes), total, available);

    private static void EnsureCurrentCpuMemory(LiteraryModelMemoryMetadata model, long projectorBytes)
    {
        var decision = LiteraryRamReservePolicy.EvaluateCurrent(GpuRequired(model, projectorBytes));
        if (!decision.Allowed)
            throw new IOException($"Insufficient physical RAM for Paddle OCR model, projector and full context: required={decision.RequiredBytes}; available={decision.AvailableBytes}.");
    }

    private static void Validate(LiteraryModelMemoryMetadata model, long projectorBytes)
    {
        if (model.Architecture != "paddleocr" || model.FileBytes != ModelBytes || projectorBytes != ProjectorBytes
            || model.BlockCount != 18 || model.ModelContextTokens != 131072 || model.PredictionLayers != 0
            || model.EmbeddingLength != 1024 || model.HeadCount != 16 || model.KvHeadCount != 2
            || model.KeyLength != 128 || model.ValueLength != 128 || model.HeadCounts is not null || model.KvHeadCounts is not null
            || model.SsmConvKernel != 0 || model.SsmInnerSize != 0 || model.SsmStateSize != 0 || model.SsmGroupCount != 0
            || model.FullAttentionInterval != 0 || model.KeyLengthSwa != 0 || model.ValueLengthSwa != 0
            || model.SlidingWindow != 0 || model.SlidingWindowPattern is not null || model.SharedKvLayers != 0)
            throw new InvalidDataException("Paddle OCR memory planner requires the exact verified 1.6 BF16 model/projector profile.");
    }
}
