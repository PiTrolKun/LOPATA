namespace AIHub.Services.LiteraryImport;

public sealed record ImportRevisionEvidenceText(string UnitId, int SourceIndex, string Message, string Parent,
    int Offset, string Text, bool Truncated, int OmittedCharacters);

public sealed record ImportRevisionEvidenceItem(string Conversation, string SourceMessage, string RequestMessage,
    string RequestParent, int RequestSourceIndex, ImportRevisionEvidenceText[] AuthorRequest,
    ImportRevisionEvidenceText[] ReplyOpening, int OmittedRequestUnits, int OmittedReplyOpeningUnits,
    int OmittedCharacters, bool Truncated);

public sealed record ImportRevisionEvidenceBatch(ImportRevisionEvidenceItem[] Items, int CandidateCount,
    int OmittedItems, int TextCharacters, int TextCharacterBudget, bool Truncated);

/// <summary>Original later replies linked to batch messages; no inferred revision decisions.</summary>
public static class ImportRevisionEvidence
{
    private const int MaxItems = 8;
    private const int MaxTextCharacters = 8000;
    private const int MaxMessageCharacters = 2000;

    public static ImportRevisionEvidenceBatch ForBatch(ImportInput input, IReadOnlyList<ImportUnit> batch)
    {
        var sourceKeys = batch.Where(u => !u.Technical && u.Message.Length > 0)
            .Select(u => (u.Conversation, u.Message)).ToHashSet();
        var indexed = input.Units.Select((unit, index) => new IndexedUnit(unit, index)).ToArray();
        var sourceEnds = indexed.Where(p => sourceKeys.Contains((p.Unit.Conversation, p.Unit.Message)))
            .GroupBy(p => (p.Unit.Conversation, p.Unit.Message)).ToDictionary(g => g.Key, g => g.Max(p => p.Index));
        var requests = indexed.Where(p => !p.Unit.Technical && p.Unit.Type == "REQUEST" && p.Unit.Message.Length > 0
                && p.Unit.Parent != p.Unit.Message && sourceEnds.ContainsKey((p.Unit.Conversation, p.Unit.Parent)))
            .GroupBy(p => (p.Unit.Conversation, p.Unit.Message, p.Unit.Parent))
            .Where(g => g.Min(p => p.Index) > sourceEnds[(g.Key.Conversation, g.Key.Parent)])
            .OrderBy(g => g.Min(p => p.Index)).ToArray();
        var remaining = MaxTextCharacters;
        var items = new List<ImportRevisionEvidenceItem>();
        foreach (var request in requests)
        {
            if (items.Count == MaxItems || remaining == 0) break;
            var requestUnits = request.OrderBy(p => p.Index).ToArray();
            var requestEnd = requestUnits[^1].Index;
            // Only the first direct assistant response, never another conversation or an earlier branch.
            var responses = indexed.Where(p => !p.Unit.Technical && p.Unit.Type == "RESPONSE"
                    && p.Unit.Conversation == request.Key.Conversation && p.Unit.Parent == request.Key.Message
                    && p.Index > requestEnd && p.Unit.Message != request.Key.Message)
                .GroupBy(p => p.Unit.Message).OrderBy(g => g.Min(p => p.Index)).FirstOrDefault();
            var openingUnits = responses?.OrderBy(p => p.Index).Take(2).ToArray() ?? [];
            var author = Capture(requestUnits, ref remaining);
            var opening = Capture(openingUnits, ref remaining);
            var omittedCharacters = requestUnits.Sum(p => p.Unit.Text.Length) + openingUnits.Sum(p => p.Unit.Text.Length)
                - author.Sum(p => p.Text.Length) - opening.Sum(p => p.Text.Length);
            var omittedRequestUnits = requestUnits.Length - author.Length;
            var omittedOpeningUnits = openingUnits.Length - opening.Length;
            items.Add(new(request.Key.Conversation, request.Key.Parent, request.Key.Message, request.Key.Parent,
                requestUnits[0].Index, author, opening, omittedRequestUnits, omittedOpeningUnits,
                omittedCharacters, omittedCharacters > 0 || omittedRequestUnits > 0 || omittedOpeningUnits > 0));
        }
        return new(items.ToArray(), requests.Length, requests.Length - items.Count,
            MaxTextCharacters - remaining, MaxTextCharacters, requests.Length > items.Count || items.Any(i => i.Truncated));
    }

    private static ImportRevisionEvidenceText[] Capture(IndexedUnit[] units, ref int remaining)
    {
        var localRemaining = Math.Min(MaxMessageCharacters, remaining);
        var result = new List<ImportRevisionEvidenceText>();
        foreach (var item in units)
        {
            if (localRemaining == 0) break;
            var take = Math.Min(localRemaining, item.Unit.Text.Length);
            if (take > 0 && take < item.Unit.Text.Length && char.IsHighSurrogate(item.Unit.Text[take - 1])) take--;
            if (take == 0 && item.Unit.Text.Length > 0) break;
            var text = item.Unit.Text[..take];
            result.Add(new(item.Unit.Id, item.Index, item.Unit.Message, item.Unit.Parent, item.Unit.Offset,
                text, take < item.Unit.Text.Length, item.Unit.Text.Length - take));
            localRemaining -= take;
            remaining -= take;
        }
        return result.ToArray();
    }

    private sealed record IndexedUnit(ImportUnit Unit, int Index);
}
