using System.IO;
using AIHub.Models;

namespace AIHub.Services;

public sealed record LiteraryActionPrompt(string Role, string Action);

public sealed record LiteraryPromptSet
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";
    public Dictionary<string, LiteraryActionPrompt> Actions { get; init; } = [];

    public LiteraryPromptSet Copy() => this with { Actions = Actions.ToDictionary(p => p.Key, p => p.Value with { }) };
}

public sealed record LiteraryPromptSettings
{
    public bool Custom { get; init; }
    public LiteraryPromptSet? Selected { get; init; }
}

/// <summary>Defaults are rebuilt from code; editing always works on a separate copy.</summary>
public static class LiteraryPromptSets
{
    public const string LegacyPrefix = "literary.studio.action.";
    public static IEnumerable<StudioAction> Actions => LiteraryStudioPrompts.Advisor.Concat(LiteraryStudioPrompts.Writer);

    public static LiteraryPromptSet Defaults(string name) => new()
    {
        Name = name,
        Actions = Actions.ToDictionary(a => a.Id, a => new LiteraryActionPrompt(
            LiteraryStudioPrompts.Writer.Any(w => w.Id == a.Id) ? LiteraryParagraphPrompts.Writer : LiteraryParagraphPrompts.Discussion,
            a.Prompt))
    };

    public static LiteraryPromptSettings Read(LiteraryStudioState state, string legacyName)
    {
        if (state.PromptSettings is { } settings) return settings with { Selected = settings.Selected?.Copy() };
        if (!Actions.Any(a => state.PromptVariants.ContainsKey(a.Id) || state.RolePromptVariants.ContainsKey(a.Id))) return new();
        var set = Defaults(legacyName);
        foreach (var action in Actions)
        {
            var pair = set.Actions[action.Id];
            set.Actions[action.Id] = new(
                state.RolePromptVariants.TryGetValue(action.Id, out var role) && !string.IsNullOrWhiteSpace(role) ? role : pair.Role,
                state.PromptVariants.TryGetValue(action.Id, out var text) && !string.IsNullOrWhiteSpace(text) ? text : pair.Action);
        }
        return new() { Custom = true, Selected = set };
    }

    public static LiteraryActionPrompt Resolve(LiteraryStudioState state, string action)
    {
        if (state.PromptSettings is not { } settings)
            return new(state.RolePromptVariants.GetValueOrDefault(action) ?? "", state.PromptVariants.GetValueOrDefault(action) ?? "");
        if (!settings.Custom) return new("", "");
        if (settings.Selected is null) throw new InvalidOperationException("PromptPairs.Empty");
        Validate([settings.Selected]);
        return settings.Selected.Actions[action];
    }

    public static bool Apply(LiteraryStudioState state, LiteraryPromptSettings settings, Func<bool> persist)
    {
        if (settings.Selected is { } selected) Validate([selected]);
        var before = state.PromptSettings;
        state.PromptSettings = settings with { Selected = settings.Selected?.Copy() };
        try { if (persist()) return true; }
        catch { state.PromptSettings = before; throw; }
        state.PromptSettings = before;
        return false;
    }

    public static bool Same(LiteraryPromptSet first, LiteraryPromptSet second) => first.Id == second.Id && first.Name == second.Name
        && first.Actions.Count == second.Actions.Count && first.Actions.All(p => second.Actions.TryGetValue(p.Key, out var value) && p.Value == value);

    public static string? LegacyAction(PromptPairPreset preset) => preset.ContractId.StartsWith(LegacyPrefix, StringComparison.Ordinal)
        && Actions.Any(a => preset.ContractId == LegacyPrefix + a.Id) ? preset.ContractId[LegacyPrefix.Length..] : null;

    public static void Validate(IReadOnlyList<LiteraryPromptSet> items)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keys = Actions.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item is null || !Guid.TryParseExact(item.Id, "N", out _) || !ids.Add(item.Id)
                || item.Actions is null || !keys.SetEquals(item.Actions.Keys)) throw new InvalidDataException("PromptPairs.InvalidData");
            if (string.IsNullOrWhiteSpace(item.Name) || item.Actions.Values.Any(p => p is null
                || string.IsNullOrWhiteSpace(p.Role) || string.IsNullOrWhiteSpace(p.Action))) throw new InvalidDataException("PromptPairs.Required");
            if (!names.Add(item.Name.Trim())) throw new InvalidDataException("PromptPairs.Duplicate");
        }
    }
}
