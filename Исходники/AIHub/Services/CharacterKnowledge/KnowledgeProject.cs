using System.Text.Json.Serialization;

namespace AIHub.Services.CharacterKnowledge;

internal sealed record KnowledgeBase(Guid BaseId, string Name, DateTimeOffset? CreatedUtc);

internal sealed record KnowledgeEntry(Guid Id, string Type, string Input, string Output)
{
    internal static KnowledgeEntry Empty() => new(Guid.NewGuid(), "", "", "");
}

internal sealed record KnowledgeRule(Guid Id, string Condition, List<KnowledgeStep> Then, List<KnowledgeStep> Else)
{
    public string Condition { get; set; } = Condition;

    internal static KnowledgeRule Empty() => new(Guid.NewGuid(), "", [], []);

    internal KnowledgeRule Copy(Guid id) =>
        new(id, Condition, Then.Select(step => step.Copy()).ToList(), Else.Select(step => step.Copy()).ToList());
}

internal sealed class KnowledgeStep(string kind)
{
    internal const string Action = "action";
    internal const string Reaction = "reaction";
    internal const string Branch = "branch";

    public string Kind { get; } = kind;
    public string? Text { get; set; }
    public string? Condition { get; set; }
    public List<KnowledgeStep>? Then { get; init; }
    public List<KnowledgeStep>? Else { get; init; }

    internal static KnowledgeStep Empty(string kind) => kind == Branch
        ? new(kind) { Condition = "", Then = [], Else = [] }
        : new(kind) { Text = "" };

    internal KnowledgeStep Copy() => new(Kind)
    {
        Text = Text,
        Condition = Condition,
        Then = Then?.Select(step => step.Copy()).ToList(),
        Else = Else?.Select(step => step.Copy()).ToList()
    };
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "editor")]
[JsonDerivedType(typeof(KnowledgeEntryDraft), "entries")]
[JsonDerivedType(typeof(KnowledgeRuleDraft), "rules")]
internal abstract record KnowledgeDraft(Guid? TargetId)
{
    [JsonIgnore]
    public abstract Guid ItemId { get; }
}

internal sealed record KnowledgeEntryDraft(KnowledgeEntry Content, Guid? TargetId = null) : KnowledgeDraft(TargetId)
{
    [JsonIgnore]
    public override Guid ItemId => Content.Id;
}

internal sealed record KnowledgeRuleDraft(KnowledgeRule Content, Guid? TargetId = null) : KnowledgeDraft(TargetId)
{
    [JsonIgnore]
    public override Guid ItemId => Content.Id;
}

internal sealed record KnowledgeContents(KnowledgeEntry[] Entries, KnowledgeRule[] Rules);

internal sealed record KnowledgeProject(KnowledgeBase Base, KnowledgeContents Knowledge, Guid Revision, KnowledgeDraft? Draft = null)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool ExternalAgentProject { get; init; }
    public int SchemaVersion { get; init; } = KnowledgeFormat.SchemaVersion;
}

internal static class KnowledgeEntryTypes
{
    internal static IReadOnlyList<string> Values { get; } =
        Array.AsReadOnly(new[] { "chat_example", "thought", "character_trait", "knowledge_or_memory", "habit" });
}

internal readonly record struct KnowledgeProgress(int Total)
{
    internal const int Rag = 200;
    internal const int FirstGoal = 1_000;
    internal const int SecondGoal = 10_000;

    internal bool IsProductive => Total >= FirstGoal;
    internal int Start => IsProductive ? FirstGoal : 0;
    internal int End => IsProductive ? SecondGoal : FirstGoal;
    internal double Fraction => (double)(Math.Min(Total, End) - Start) / (End - Start);
}
