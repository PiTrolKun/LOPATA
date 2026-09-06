using System.Text.Json.Serialization;

namespace AIHub.Models;

// Shared by scenarios; runtime-specific instructions belong to the scenario adapter.
public sealed record PromptPairPreset
{
    [JsonRequired] public string Id { get; init; } = Guid.NewGuid().ToString("N");
    [JsonRequired] public string ContractId { get; init; } = string.Empty;
    [JsonRequired] public string Name { get; init; } = string.Empty;
    [JsonRequired] public string AnalysisPrompt { get; init; } = string.Empty;
    [JsonRequired] public string ComposePrompt { get; init; } = string.Empty;
}

public static class PromptModes
{
    public const string Standard = "standard";
    public const string Custom = "custom";
}
