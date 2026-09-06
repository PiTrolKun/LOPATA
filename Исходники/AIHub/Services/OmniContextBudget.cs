namespace AIHub.Services;

public static class OmniContextBudget
{
    public const int ResponseReserveTokens = 4096;
    // llama.cpp b9442's Qwen3VL projector limit, explicitly pinned in launch arguments.
    public const int ImageTokenUpperBound = 4096;

    public static int Boundary(int contextTokens) => checked((int)((long)contextTokens * 95 / 100));

    public static int OutputBudget(int inputTokens, int contextTokens)
    {
        if (contextTokens <= 0 || inputTokens < 0)
            throw new ArgumentOutOfRangeException(nameof(contextTokens));
        var boundary = Boundary(contextTokens);
        if ((long)inputTokens + ResponseReserveTokens >= boundary)
            throw new ImageAnalysisContextExhaustedException(
                $"Context admission rejected: input={inputTokens}; reserve={ResponseReserveTokens}; boundary={boundary}; context={contextTokens}.");
        return boundary - inputTokens;
    }
}
