namespace AIHub.Services;

public enum LiteraryJellyField { Subject, Relation, Value, Kind, Evidence }
public sealed record LiteraryJellyIssue(LiteraryJellyField Field, string MessageKey);

/// <summary>One set of field checks for both the review UI and durable memory writes.</summary>
public static class LiteraryJellyValidation
{
    public static IReadOnlyList<LiteraryJellyIssue> Check(LiteraryJellyFact fact, string source)
    {
        if (!fact.Accepted) return [];
        var issues = new List<LiteraryJellyIssue>();
        Text(LiteraryJellyField.Subject, fact.Subject, 200, true);
        Text(LiteraryJellyField.Relation, fact.Relation, 300, true);
        Text(LiteraryJellyField.Value, fact.Value, 600, false);
        if (!LiteraryJellyContract.Kinds.Contains(fact.Kind))
            issues.Add(new(LiteraryJellyField.Kind, "Literary.Jelly.Error.Kind"));
        if (string.IsNullOrWhiteSpace(fact.Evidence))
            issues.Add(new(LiteraryJellyField.Evidence, "Literary.Jelly.Error.EvidenceRequired"));
        else if (!source.Contains(fact.Evidence, StringComparison.Ordinal))
            issues.Add(new(LiteraryJellyField.Evidence, "Literary.Jelly.Error.ExactQuote"));
        return issues;

        void Text(LiteraryJellyField field, string text, int limit, bool required)
        {
            if (required && string.IsNullOrWhiteSpace(text))
                issues.Add(new(field, "Literary.Jelly.Error.Required"));
            else if (text.Length > limit)
                issues.Add(new(field, "Literary.Jelly.Error.TooLong"));
        }
    }
}
