using System.IO;
using System.Text.Json;

namespace AIHub.Services;

/// <summary>Unconfirmed review edits stay separate from the canonical memory.</summary>
public sealed class LiteraryJellyReviewDraft(LiteraryProjectLayout layout, LiteraryJellyBatch batch)
{
    private string FilePath => Path.Combine(layout.EnsureFolder("Jelly/ReviewDrafts"), Guid.Parse(batch.Id).ToString("N") + ".json");
    public IReadOnlyList<LiteraryJellyFact> Read()
    {
        new LiteraryJellyStore(layout).VerifySource(batch);
        var path = FilePath; LiteraryProjectLayout.CheckTreePath(path);
        if (!File.Exists(path)) return batch.Facts;
        var draft = JsonSerializer.Deserialize<Review>(LiteraryChapterFiles.Read(path)) ?? throw new InvalidDataException("Invalid memory review draft.");
        if (draft.BatchId != batch.Id || draft.Revision != batch.Revision) throw new IOException("Memory review source changed.");
        Validate(draft.Facts); return draft.Facts;
    }
    public void Save(IReadOnlyList<LiteraryJellyFact> facts)
    {
        new LiteraryJellyStore(layout).VerifySource(batch); Validate(facts);
        var path = FilePath; LiteraryProjectLayout.CheckTreePath(path);
        LiteraryChapterFiles.Write(path, JsonSerializer.Serialize(new Review(batch.Id, batch.Revision, facts.ToArray())));
    }
    private void Validate(IReadOnlyList<LiteraryJellyFact>? facts)
    {
        if (facts is null || facts.Count != batch.Facts.Length || facts.Any(f => f is null || f.Subject is null || f.Relation is null
            || f.Value is null || f.Evidence is null || f.Kind is null) || !facts.Select(f => f.Id).SequenceEqual(batch.Facts.Select(f => f.Id)))
            throw new InvalidDataException("Memory review draft does not match the original proposal.");
    }
    private sealed record Review(string BatchId, string Revision, LiteraryJellyFact[] Facts);
}
