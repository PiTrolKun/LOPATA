namespace AIHub.Services.CharacterKnowledge;

internal static class KnowledgeEditing
{
    internal static KnowledgeDraft FromEntry(KnowledgeEntry entry, bool asVariant) => asVariant
        ? new KnowledgeEntryDraft(entry with { Id = Guid.NewGuid() })
        : new KnowledgeEntryDraft(entry, entry.Id);

    internal static KnowledgeDraft FromRule(KnowledgeRule rule, bool asVariant) => asVariant
        ? new KnowledgeRuleDraft(rule.Copy(Guid.NewGuid()))
        : new KnowledgeRuleDraft(rule.Copy(rule.Id), rule.Id);

    internal static KnowledgeContents Commit(KnowledgeContents contents, KnowledgeDraft draft)
    {
        if (draft is KnowledgeEntryDraft entryDraft)
        {
            var entry = entryDraft.Content;
            KnowledgeFormat.ValidateEntry(entry, complete: true);
            var key = KnowledgeFormat.EntryKey(entry);
            if (contents.Entries.Any(other => other.Id != entry.Id && KnowledgeFormat.EntryKey(other) == key))
                throw new KnowledgeProblem("DuplicateEntry");
            return contents with { Entries = Upsert(contents.Entries, entry, item => item.Id == entry.Id) };
        }

        var ruleDraft = (KnowledgeRuleDraft)draft;
        var rule = ruleDraft.Content;
        KnowledgeFormat.ValidateRule(rule, complete: true);
        var ruleKey = KnowledgeFormat.RuleKey(rule);
        if (contents.Rules.Any(other => other.Id != rule.Id && KnowledgeFormat.RuleKey(other) == ruleKey))
            throw new KnowledgeProblem("DuplicateRule");
        return contents with { Rules = Upsert(contents.Rules, rule.Copy(rule.Id), item => item.Id == rule.Id) };
    }

    private static T[] Upsert<T>(T[] items, T replacement, Predicate<T> matches)
    {
        var index = Array.FindIndex(items, matches);
        if (index < 0) return [.. items, replacement];
        var updated = (T[])items.Clone();
        updated[index] = replacement;
        return updated;
    }
}
