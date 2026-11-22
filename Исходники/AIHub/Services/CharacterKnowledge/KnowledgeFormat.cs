using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace AIHub.Services.CharacterKnowledge;

internal sealed class KnowledgeProblem(string key, string? item = null) : IOException(key)
{
    internal string Key { get; } = key;
    internal string? Item { get; } = item;
}

internal static class KnowledgeFormat
{
    internal const int SchemaVersion = 1;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        AllowOutOfOrderMetadataProperties = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        WriteIndented = true,
        MaxDepth = 256
    };

    internal static T Read<T>(Stream stream) where T : class =>
        JsonSerializer.Deserialize<T>(stream, Json) ?? throw new KnowledgeProblem("InvalidData");

    internal static void Validate(KnowledgeProject project)
    {
        if (project.SchemaVersion != SchemaVersion) throw new KnowledgeProblem("UnsupportedVersion");
        if (project.Base.BaseId == Guid.Empty || string.IsNullOrWhiteSpace(project.Base.Name))
            throw new KnowledgeProblem("InvalidData");

        var ids = new HashSet<Guid>();
        var entries = new HashSet<(string, string, string)>();
        foreach (var entry in project.Knowledge.Entries)
        {
            if (entry is null || entry.Id == Guid.Empty || !ids.Add(entry.Id))
                throw new KnowledgeProblem("InvalidData");
            ValidateEntry(entry, complete: true);
            if (!entries.Add(EntryKey(entry)))
                throw new KnowledgeProblem("DuplicateArchiveEntry", entry.Input);
        }

        var rules = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in project.Knowledge.Rules)
        {
            if (rule is null || rule.Id == Guid.Empty || !ids.Add(rule.Id))
                throw new KnowledgeProblem("InvalidData");
            ValidateRule(rule, complete: true);
            if (!rules.Add(RuleKey(rule)))
                throw new KnowledgeProblem("DuplicateArchiveRule", rule.Condition);
        }

        switch (project.Draft)
        {
            case KnowledgeEntryDraft entry:
                if (entry.TargetId is { } entryId && !project.Knowledge.Entries.Any(saved => saved.Id == entryId))
                    throw new KnowledgeProblem("InvalidData");
                ValidateEntry(entry.Content, complete: false);
                break;
            case KnowledgeRuleDraft rule:
                if (rule.TargetId is { } ruleId && !project.Knowledge.Rules.Any(saved => saved.Id == ruleId))
                    throw new KnowledgeProblem("InvalidData");
                ValidateRule(rule.Content, complete: false);
                break;
        }

        if (project.Draft is { } draft && (draft.ItemId == Guid.Empty ||
            (draft.TargetId is { } target ? target != draft.ItemId : ids.Contains(draft.ItemId))))
            throw new KnowledgeProblem("InvalidData");
    }

    internal static void ValidateEntry(KnowledgeEntry entry, bool complete)
    {
        if (!complete && entry.Type.Length == 0) return;
        if (!KnowledgeEntryTypes.Values.Contains(entry.Type) ||
            (complete && (string.IsNullOrWhiteSpace(entry.Input) || string.IsNullOrWhiteSpace(entry.Output))))
            throw new KnowledgeProblem("EntryIncomplete");
    }

    internal static void ValidateRule(KnowledgeRule rule, bool complete) =>
        ValidateBranch(rule.Condition, rule.Then, rule.Else, complete);

    private static void ValidateBranch(string condition, List<KnowledgeStep> then, List<KnowledgeStep> otherwise, bool complete)
    {
        if (complete && (string.IsNullOrWhiteSpace(condition) || then.Count + otherwise.Count == 0))
            throw new KnowledgeProblem("RuleIncomplete");
        foreach (var step in then.Concat(otherwise))
        {
            switch (step)
            {
                case { Kind: KnowledgeStep.Action or KnowledgeStep.Reaction, Text: { } text,
                    Condition: null, Then: null, Else: null }:
                    if (complete && string.IsNullOrWhiteSpace(text)) throw new KnowledgeProblem("RuleIncomplete");
                    break;
                case { Kind: KnowledgeStep.Branch, Text: null, Condition: { } nested, Then: { } yes, Else: { } no }:
                    ValidateBranch(nested, yes, no, complete);
                    break;
                default:
                    throw new KnowledgeProblem("InvalidData");
            }
        }
    }

    internal static (string, string, string) EntryKey(KnowledgeEntry entry) =>
        (entry.Type, Normalize(entry.Input), Normalize(entry.Output));

    internal static string RuleKey(KnowledgeRule rule) =>
        JsonSerializer.Serialize(new KnowledgeRule(Guid.Empty, Normalize(rule.Condition),
            rule.Then.Select(NormalizeStep).ToList(), rule.Else.Select(NormalizeStep).ToList()), Json);

    private static KnowledgeStep NormalizeStep(KnowledgeStep step) => new(step.Kind)
    {
        Text = step.Text is { } text ? Normalize(text) : null,
        Condition = step.Condition is { } condition ? Normalize(condition) : null,
        Then = step.Then?.Select(NormalizeStep).ToList(),
        Else = step.Else?.Select(NormalizeStep).ToList()
    };

    private static string Normalize(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
