using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHub.Models;

namespace AIHub.Services;

public sealed record MusicPoetryReasoningPreference
{
    public string ModeId { get; init; } = "";
    public int? Budget { get; init; }
    public string? Effort { get; init; }
    public bool? PreserveThoughts { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Additional { get; init; }
}

public sealed record MusicPoetryReasoningOptions(bool Enabled, int Budget, string? Effort = null, bool PreserveThoughts = false,
    bool HistoryControl = false)
{
    public static MusicPoetryReasoningOptions Disabled { get; } = new(false, 0);
    public int ReplyReserve(int capacity) => checked(Math.Min(4096, capacity / 3) + Budget);
    public Dictionary<string, object> TemplateOptions()
    {
        var result = new Dictionary<string, object> { ["enable_thinking"] = Enabled };
        if (Effort is not null) result["reasoning_effort"] = Effort;
        if (HistoryControl) result["preserve_thinking"] = Enabled && PreserveThoughts;
        return result;
    }
}

/// <summary>Explicitly tested model controls; unknown saved choices never silently fall back.</summary>
public static class MusicPoetryReasoning
{
    public const int CoreBudget = 2048;
    public const int GemmaBudget = 4096;
    public const string DefaultMode = "on";
    public static bool SupportsId(string? modelId) => modelId is "core" or "profile-kaine" or "profile-abliterated" or "beta" or "gamma" or "executor" or "profile-gemma-poetry";
    public static bool Extended(string? modelId) => modelId is "gamma" or "executor";
    public static int DefaultBudget(string? modelId) => modelId == "profile-gemma-poetry" ? GemmaBudget : CoreBudget;
    public static string? ModelId(DebugModelInfo? model)
    {
        var id = model is null ? null : MusicPoetryModelCatalog.Find(model)?.Id;
        return SupportsId(id) ? id : null;
    }
    public static bool Supports(DebugModelInfo? model) => ModelId(model) is not null;
    public static MusicPoetryReasoningOptions Resolve(DebugModelInfo? model, string? modeId = null, MusicPoetryReasoningPreference? preference = null)
    {
        if (!Supports(model)) return MusicPoetryReasoningOptions.Disabled;
        return ResolveId(ModelId(model)!, preference ?? new() { ModeId = modeId ?? DefaultMode });
    }
    public static MusicPoetryReasoningOptions ResolveId(string id, MusicPoetryReasoningPreference preference)
    {
        if (!SupportsId(id) || preference.ModeId is not ("on" or "off"))
            throw new InvalidDataException("Music.Poetry.ReasoningChoiceUnavailable");
        var budget = preference.Budget ?? DefaultBudget(id);
        var effort = id == "gamma" ? preference.Effort ?? "medium" : null;
        if (budget != DefaultBudget(id) && (!Extended(id) || budget != 4096)
            || id == "gamma" && effort is not ("medium" or "xhigh")
            || id != "gamma" && preference.Effort is not null
            || !Extended(id) && preference.PreserveThoughts is not null)
            throw new InvalidDataException("Music.Poetry.ReasoningChoiceUnavailable");
        return preference.ModeId switch
        {
            "on" => new(true, budget, effort, Extended(id) && (preference.PreserveThoughts ?? true), Extended(id)),
            "off" => new(false, 0, effort, false, Extended(id)),
            _ => throw new InvalidDataException("Music.Poetry.ReasoningChoiceUnavailable")
        };
    }
}
