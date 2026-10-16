namespace AIHub.Services;

/// <summary>Bounded request-local controls for references, generation and execution.</summary>
public static class MusicRhymeModelSettings
{
    public static IReadOnlyList<MusicRhymeSetting> Items { get; } = [
        MusicRhymeSetting.Int("RagRawHits", "rag", 5, 1, 50),
        MusicRhymeSetting.Int("RagStoredHits", "rag", 2, 0, 50),
        MusicRhymeSetting.Int("RagStoredChars", "rag", 900, 50, 4000),
        MusicRhymeSetting.Int("RagPromptHits", "rag", 2, 0, 50),
        MusicRhymeSetting.Int("RagPromptChars", "rag", 450, 50, 4000),
        MusicRhymeSetting.Int("PromptCandidates", "rag", 24, 0, 1000),
        MusicRhymeSetting.Int("StanzaContextLines", "rag", 32, 0, 2000),
        MusicRhymeSetting.Info("RagQueryChars", "rag", 2000),
        MusicRhymeSetting.Info("RagQueryTokens", "rag", 512),
        MusicRhymeSetting.Int("ModelContext", "model", 32768, 1024, 32768),
        MusicRhymeSetting.Int("ReplyTokens", "model", 8192, 128, 32768),
        MusicRhymeSetting.Number("ReplyFraction", "model", .5, .05, .9),
        MusicRhymeSetting.Toggle("Reasoning", "model", true),
        MusicRhymeSetting.Int("ReasoningBudget", "model", 0, 0, 8192),
        MusicRhymeSetting.Number("Temperature", "model", .5, 0, 2),
        MusicRhymeSetting.Toggle("SamplingOverrides", "model", false),
        MusicRhymeSetting.Number("TopP", "model", .95, .01, 1),
        MusicRhymeSetting.Int("TopK", "model", 40, 0, 1000),
        MusicRhymeSetting.Number("MinP", "model", 0, 0, 1),
        MusicRhymeSetting.Number("RepeatPenalty", "model", 1, .5, 2),
        MusicRhymeSetting.Int("MaxAnswerChars", "model", 200000, 1000, 200000),
        MusicRhymeSetting.Toggle("WholeStanzas", "model", true),
        MusicRhymeSetting.Info("ReplyVersions", "model", 1),
        MusicRhymeSetting.Info("ReplyStructure", "model", 1),
        MusicRhymeSetting.Info("BinContext", "model", 8192),
        MusicRhymeSetting.Int("GenerationTimeoutSeconds", "execution", 0, 0, 86400),
        MusicRhymeSetting.Int("RagTimeoutSeconds", "execution", 0, 0, 86400),
        MusicRhymeSetting.Int("ServerStartSeconds", "execution", 300, 15, 1800),
        MusicRhymeSetting.Info("ParallelOperations", "execution", 1),
        MusicRhymeSetting.Info("ParallelModelRequests", "execution", 1),
        MusicRhymeSetting.Info("HardwareRetries", "execution", 1),
        MusicRhymeSetting.Info("QualityRetries", "execution", 0),
        MusicRhymeSetting.Info("GpuStartupPercent", "execution", 97),
        MusicRhymeSetting.Info("RagRecovery", "execution", 1)
    ];

    public static int Reserve(MusicRhymeTuning tuning, int capacity) => Math.Max(1,
        Math.Min(tuning.Integer("ReplyTokens"), (int)(capacity * tuning.Number("ReplyFraction"))));

    public static MusicPoetryReasoningOptions ReasoningOptions(Models.DebugModelInfo model, MusicRhymeTuning tuning)
    {
        var options = MusicPoetryReasoning.Resolve(model, tuning.Enabled("Reasoning") ? "on" : "off");
        return options.Enabled && tuning.Integer("ReasoningBudget") > 0
            ? options with { Budget = tuning.Integer("ReasoningBudget") } : options;
    }

    public static (double TopP, int TopK, double MinP, double RepeatPenalty) Sampling(
        MusicRhymeTuning tuning, MusicPoetryProfile? profile) => tuning.Enabled("SamplingOverrides")
        ? (tuning.Number("TopP"), tuning.Integer("TopK"), tuning.Number("MinP"), tuning.Number("RepeatPenalty"))
        : (profile?.TopP ?? .95, profile?.TopK ?? 40, profile?.MinP ?? 0, profile?.RepeatPenalty ?? 1);
}
