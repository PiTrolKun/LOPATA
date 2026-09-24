using System.IO;
using System.Text.Json;

namespace AIHub.Services.LiteraryImport;

/// <summary>Checks imported memory against the published, corrected working text.</summary>
public static class ImportJellySource
{
    public static void Verify(LiteraryProjectLayout layout, LiteraryJellyBatch batch)
    {
        layout.EnsurePresent();
        var manifest = JsonSerializer.Deserialize<ImportWorkingPartsManifest>(
            LiteraryChapterFiles.Read(Path.Combine(layout.Root, "WorkingParts/manifest.json")), ImportJson.Options)
            ?? throw new InvalidDataException("Missing working generation.");
        if (manifest.Version != 1 || manifest.ProjectId != layout.ProjectId || manifest.Generation != batch.ImportGeneration
            || manifest.Generation.Length != 64 || !manifest.Generation.All(Uri.IsHexDigit)
            || manifest.BookRevision != ImportSession.Hash(new ImportReviewBookStore(layout.Root).Load().Text))
            throw new IOException("The imported memory source changed.");
        var part = manifest.Parts.SingleOrDefault(p => p.Id == batch.PartId);
        if (part is null || !Guid.TryParseExact(part.Id, "N", out _) || part.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) != batch.Number)
            throw new InvalidDataException("Unknown working part.");
        var path = Path.Combine(layout.Root, "WorkingParts", manifest.Generation, part.Id + ".txt");
        LiteraryProjectLayout.CheckTreePath(path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
            || !manifest.Hashes.TryGetValue(part.Id + ".txt", out var hash) || ImportSession.HashFile(path) != hash
            || LiteraryChapterFiles.Read(path) != batch.SourceText || LiteraryWorkIndex.Revision(batch.SourceText) != batch.Revision)
            throw new IOException("The working source changed.");
        // Once handed over to the workspace, its active draft must retain the usual exclusion.
        var chapters = new LiteraryChapterStore(layout.Root); chapters.Open();
        if (chapters.Index.ActiveId == batch.PartId) throw new IOException("The active draft is not a memory source.");
        var working = chapters.Index.Parts.SingleOrDefault(p => p.Id == batch.PartId);
        if (working is not null && LiteraryChapterFiles.Read(Path.Combine(layout.Root, "chapters", working.FileName)) != batch.SourceText)
            throw new IOException("The working memory source changed.");
    }
}
