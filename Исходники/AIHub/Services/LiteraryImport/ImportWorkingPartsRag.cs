using System.IO;
using System.Text.Json;

namespace AIHub.Services.LiteraryImport;

public sealed record ImportWorkingRagLink(string PointId, string PartId, int BookStart, int PartStart, int Length);

/// <summary>Exact intersections with the existing book vectors, including points crossing part boundaries.</summary>
public static class ImportWorkingPartsRag
{
    public static (ImportRagManifest Manifest, string Vectors) RequireCurrent(string root, ImportReviewBook book)
    {
        var path = Path.Combine(root, "Rag/Book/manifest.json");
        var manifest = JsonSerializer.Deserialize<ImportRagManifest>(LiteraryChapterFiles.Read(path))
            ?? throw new InvalidDataException("Missing book index.");
        var revision = ImportSession.Hash(book.Text);
        if (manifest.Area != "Book" || manifest.Revision != revision || manifest.ModelRevision != GigaEmbeddingInstallation.Revision
            || manifest.Characters != book.Text.Length || !Guid.TryParseExact(manifest.Collection, "N", out _))
            throw new InvalidDataException("Book index must be prepared again.");
        var folder = Path.Combine(root, "Rag/ImportIndexes/Book", revision);
        var vectors = Path.Combine(folder, "vectors.jsonl");
        if (ImportSession.HashFile(vectors) != manifest.VectorsHash || ImportSession.HashFile(Path.Combine(folder, "text.json")) != manifest.InputHash)
            throw new InvalidDataException("Book index checksum differs.");
        return (manifest, vectors);
    }

    public static List<ImportWorkingRagLink> Build(ImportWorkingPartsPlan plan, ImportReviewBook book,
        string vectors, int points, CancellationToken ct)
    {
        plan.Validate(book);
        var input = new ImportRagInput("Book", plan.BookRevision,
            [new(book.ProjectId, "book", book.Text, "project")], []);
        ImportRagArtifact.Validate(vectors, input, points, ct);
        var offsets = new List<int>(); var pos = 0;
        foreach (var rune in book.Text.EnumerateRunes()) { offsets.Add(pos); pos += rune.Utf16SequenceLength; }
        offsets.Add(pos);
        var result = new List<ImportWorkingRagLink>();
        foreach (var line in File.ReadLines(vectors))
        {
            ct.ThrowIfCancellationRequested();
            using var json = JsonDocument.Parse(line);
            var point = json.RootElement; var payload = point.GetProperty("payload");
            var start = offsets[payload.GetProperty("offset").GetInt32()];
            var end = start + payload.GetProperty("text").GetString()!.Length;
            foreach (var part in plan.Parts.Where(p => p.Start < end && p.Start + p.Length > start))
            {
                var from = Math.Max(start, part.Start); var to = Math.Min(end, part.Start + part.Length);
                result.Add(new(point.GetProperty("id").ToString(), part.Id, from, from - part.Start, to - from));
            }
        }
        return result;
    }

    // The future scenario adapter must use these clipped passages, never the whole crossing point.
    public static IReadOnlyList<ImportWorkingRagLink> Visible(IEnumerable<ImportWorkingRagLink> links, string pointId, string activeId)
        => links.Where(l => l.PointId == pointId && l.PartId != activeId).ToArray();
}
