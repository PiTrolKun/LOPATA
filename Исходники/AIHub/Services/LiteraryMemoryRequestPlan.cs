namespace AIHub.Services;

/// <summary>Author constraints stay verbatim; only research evidence may be condensed.</summary>
public sealed class LiteraryMemoryRequestPlan
{
    private readonly ParagraphEvidence _original;
    private readonly ParagraphMaterial[] _mandatory;
    private readonly HashSet<string> _mandatoryIds;
    public ParagraphEvidence SearchEvidence { get; }

    public LiteraryMemoryRequestPlan(ParagraphEvidence original)
    {
        _original = original;
        _mandatory = original.Materials.Where(m => m.Kind is "author_anchor" or "confirmed_creation_intent" or "future_route").ToArray();
        _mandatoryIds = _mandatory.Select(m => m.Id).ToHashSet();
        SearchEvidence = new(original.Materials.Where(m => !_mandatoryIds.Contains(m.Id)).ToArray(),
            original.Receipts.Select(r => r with { Materials = r.Materials.Where(id => !_mandatoryIds.Contains(id)).ToArray() }).ToArray());
    }

    public ParagraphEvidence Combine(ParagraphEvidence searched)
    {
        var receipts = searched.Receipts.ToDictionary(r => r.Id);
        return new([.. _mandatory, .. searched.Materials], _original.Receipts.Select(r =>
        {
            var result = receipts.GetValueOrDefault(r.Id);
            return r with
            {
                Materials = r.Materials.Where(_mandatoryIds.Contains).Concat(result?.Materials ?? []).Distinct().ToArray(),
                Status = result?.Status ?? r.Status,
                Detail = result?.Detail ?? r.Detail
            };
        }).ToArray());
    }
}
