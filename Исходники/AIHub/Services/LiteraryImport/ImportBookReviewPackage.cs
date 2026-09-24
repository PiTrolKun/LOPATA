using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.LiteraryImport;

/// <summary>Export the reviewable book and audit trail without starting RAG or Jelly.</summary>
public static class ImportBookReviewPackage
{
    public static string Export(ImportSession session, LiteraryProjectEntry entry, string language, CancellationToken ct)
    {
        var confirmed = session.State.Stage == "book-confirmed";
        var folder = new LiteraryProjectLayout(entry.ProjectPath).EnsureFolder("Exports/Import");
        var original = Path.Combine(folder, "original" + session.State.SourceExtension);
        if (!File.Exists(original))
        {
            var temporary = original + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.Copy(session.Source, temporary); ct.ThrowIfCancellationRequested(); File.Move(temporary, original); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        if (ImportSession.HashFile(original) != session.State.SourceHash)
            throw new InvalidDataException("Literary.Import.Corrupt");
        foreach (var audit in Directory.EnumerateFiles(Path.Combine(entry.ProjectPath, "Import"), "review-*.json"))
        {
            var step = "human-review/" + Path.GetFileName(audit);
            if (!session.State.Artifacts.Any(a => a.Step == step)) session.AddBytes(step, File.ReadAllBytes(audit));
        }
        if (File.Exists(Path.Combine(entry.ProjectPath, "Import", "book-review.json")))
            session.AddJson("review-book-snapshot", new ImportReviewBookStore(entry.ProjectPath).Load());
        else
        {
            var chapters = new LiteraryChapterStore(entry.ProjectPath); chapters.Open();
            session.AddJson("review-text-snapshot", chapters.Snapshot());
            session.AddJson("review-state", JsonSerializer.Deserialize<ImportReviewFile>(
                File.ReadAllText(Path.Combine(entry.ProjectPath, "Import", "review.json")), ImportJson.Options));
        }
        session.AddJson("preparation-answers", new ImportPreparationAnswers(session.Root).Values);
        var book = Path.Combine(folder, "book.docx");
        ImportBookExporter.Export(entry.ProjectPath, book, language == "ru", ct);
        ImportBlackBox.Export(session, Path.Combine(folder, "import-history.xlsx"), ct);
        session.State.Stage = confirmed ? "book-confirmed" : "book-review"; session.Save();
        return book;
    }
}
