using System.IO;
using System.Text.Json;

namespace AIHub.Services.LiteraryImport;

/// <summary>Reuses book embeddings; every returned passage is clipped to its owned working part.</summary>
public static class ImportWorkspaceIndexes
{
    public static IEnumerable<(LiteraryWorkManifest Manifest, string Vectors)> Build(string root,
        ImportWorkingPartsManifest parts, LiteraryChapterIndex index)
    {
        var book = new ImportReviewBookStore(root).Load();
        var rag = ImportWorkingPartsRag.RequireCurrent(root, book);
        var plan = new ImportWorkingPartsPlan { ProjectId = parts.ProjectId, BookRevision = parts.BookRevision,
            Mode = parts.Mode, Parts = parts.Parts.ToList() };
        // Recompute intersections from verified vectors, not unchecked stored offsets.
        var links = ImportWorkingPartsRag.Build(plan, book, rag.Vectors, rag.Manifest.Points, default).ToLookup(l => l.PartId);
        var points = File.ReadLines(rag.Vectors).Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .ToDictionary(p => p.GetProperty("id").ToString());
        foreach (var part in parts.Parts)
        {
            var chapter = index.Parts.Single(p => p.Id == part.Id);
            var text = book.Text.Substring(part.Start, part.Length);
            var number = $"{chapter.Chapter:000}" + (chapter.Part == 1 ? "" : "." + chapter.Part);
            var rows = links[part.Id].Select((link, i) => JsonSerializer.Serialize(new
            {
                id = i + 1, vector = points[link.PointId].GetProperty("vector"),
                payload = new { source = part.Id, section = number, kind = "project",
                    offset = text[..link.PartStart].EnumerateRunes().Count(), text = text.Substring(link.PartStart, link.Length) }
            })).ToArray();
            if (rows.Length == 0) throw new InvalidDataException("Missing working vectors.");
            var collection = ImportSession.Hash("import-work-v1\n" + parts.ProjectId + parts.Generation + part.Id)[..32].ToLowerInvariant();
            yield return (new(part.Id, LiteraryWorkIndex.Revision(text), collection, rows.Length,
                GigaEmbeddingInstallation.Revision + ImportEligibility.Fingerprint(root, part.Id)), string.Join("\n", rows) + "\n");
        }
    }
}
