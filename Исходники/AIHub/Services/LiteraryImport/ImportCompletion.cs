using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.LiteraryImport;

public static class ImportCompletion
{
    public static async Task CompleteAsync(ImportSession session, LiteraryProjectEntry entry, LiteraryChatRuntime runtime,
        string language, IProgress<ImportProgress> progress, CancellationToken ct)
    {
        session.State.LastError = ""; session.Save();
        try { await PrepareAsync(session, entry, runtime, progress, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested)
        {
            if (session.State.Stage == "rag") session.State.RagStatus = "failed";
            else if (session.State.Stage == "memory") session.State.MemoryStatus = "failed";
            else throw;
            session.State.LastError = ex.GetType().Name + ": " + ex.Message;
            session.Add("preparation-failure", ex.ToString(), "error"); session.Save();
        }
        progress.Report(new("Export", 0, 0));
        Export(session, entry, language, ct);
        progress.Report(new(session.State.Stage == "complete" ? "Complete" : "Partial", 1, 1));
    }
    private static async Task PrepareAsync(ImportSession session, LiteraryProjectEntry entry, LiteraryChatRuntime runtime,
        IProgress<ImportProgress> progress, CancellationToken ct)
    {
        var layout = new LiteraryProjectLayout(entry.ProjectPath); layout.EnsurePresent();
        var reviewPath = Path.Combine(entry.ProjectPath, "Import", "review.json");
        var reviewRevision = CurrentRevision(entry.ProjectPath);
        if (session.State.ReviewRevision != reviewRevision)
        {
            session.State.RagStatus = "pending"; session.State.MemoryStatus = "pending";
            session.State.ReviewRevision = reviewRevision; session.Save();
        }
        var review = JsonSerializer.Deserialize<ImportReviewFile>(File.ReadAllText(reviewPath), ImportJson.Options)!;
        var excluded = review.Parts.Count(p => p.Doubts.Length > 0);
        var bridge = new InlineProgress<LiteraryPreparationProgress>(p => progress.Report(new("Rag", (int)Math.Max(0, p.Percent), p.Percent >= 0 ? 100 : 0)));
        if (!session.State.RagStatus.StartsWith("ready", StringComparison.Ordinal))
        {
            session.State.Stage = "rag"; session.State.RagStatus = "pending"; session.Save();
            runtime.Stop(); // Release the same GPU model before preparing embeddings.
            await new LiteraryWorkIndex(layout).PrepareAsync(bridge, ct);
            session.State.RagStatus = excluded == 0 ? "ready" : "ready-with-exclusions"; session.Save();
            session.AddJson("rag-result", new { status = session.State.RagStatus, partsWithExclusions = excluded, policy = "reviewed spans only" });
        }
        if (!session.State.MemoryStatus.StartsWith("proposals-ready", StringComparison.Ordinal))
        {
            session.State.Stage = "memory"; session.State.MemoryStatus = "pending"; session.Save();
            progress.Report(new("Memory", 0, 0));
            var extraction = new LiteraryJellyPreparation(layout, async (text, token) =>
            {
                var step = "memory/" + ImportSession.Hash(text);
                if (session.ReadLast<string>(step + "/parsed") is { } cached) return cached;
                var raw = await runtime.ImportAnalyzeAsync([
                    new() { Role = "system", Content = LiteraryJellyContract.Instruction },
                    new() { Role = "user", Content = text }], session, step, 3072, token);
                var json = LiteraryStructuredReply.Json(raw);
                var facts = LiteraryJellyContract.Parse(json);
                // These are pending proposals, not trusted facts. Preserve field errors for
                // the existing review editor; Confirm still rejects an invalid exact quote.
                session.AddJson(step + "/validation", facts.Select((fact, i) => new { i, issues = LiteraryJellyValidation.Check(fact, text) }));
                session.AddJson(step + "/parsed", json); return json;
            }, "runeweaver");
            var memoryProgress = new InlineProgress<LiteraryPreparationProgress>(p => progress.Report(new("Memory", (int)Math.Max(0, p.Percent), p.Percent >= 0 ? 100 : 0)));
            await extraction.PrepareAsync((batch, _) => { session.AddJson("memory-proposals", batch); return Task.FromResult(false); }, memoryProgress, ct, prepareAllPending: true);
            new LiteraryJellyStore(layout).Initialize(); // Valid empty DB when every part awaits review; never invent facts.
            session.State.MemoryStatus = excluded == 0 ? "proposals-ready" : "proposals-ready-with-exclusions"; session.Save();
            session.AddJson("memory-result", new { status = session.State.MemoryStatus, partsAwaitingTextReview = excluded, requiresManualFactApproval = true });
        }
    }
    public static void Export(ImportSession session, LiteraryProjectEntry entry, string language, CancellationToken ct)
    {
        var layout = new LiteraryProjectLayout(entry.ProjectPath); layout.EnsurePresent();
        if (session.State.ReviewRevision.Length > 0 && session.State.ReviewRevision != CurrentRevision(entry.ProjectPath))
        { session.State.RagStatus = "pending"; session.State.MemoryStatus = "pending"; }
        var folder = layout.EnsureFolder("Exports/Import");
        session.State.Stage = "export"; session.Save();
        var status=ImportProjectStatus.Read(entry.ProjectPath);
        session.State.RagStatus=status.MissingIndexes>0?"pending":status.Excluded>0?"ready-with-exclusions":"ready";
        session.State.MemoryStatus=status.MissingMemory>0?"pending":status.Excluded>0?"proposals-ready-with-exclusions":"proposals-ready";
        var memoryAudit=JsonSerializer.Serialize(new LiteraryJellyStore(layout).ExportAudit(),ImportJson.Options);
        var memoryKey="jelly-review-snapshot/"+ImportSession.Hash(memoryAudit);
        if(!session.State.Artifacts.Any(a=>a.Step==memoryKey)) session.Add(memoryKey,memoryAudit);
        // The original export and the self-contained workbook travel with the resulting project.
        var source = Path.Combine(folder, "original" + session.State.SourceExtension);
        if (!File.Exists(source))
        {
            var temporary = source + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.Copy(session.Source, temporary); ct.ThrowIfCancellationRequested(); File.Move(temporary, source); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        if (ImportSession.HashFile(source) != session.State.SourceHash) throw new InvalidDataException("Literary.Import.Corrupt");
        ImportBookExporter.Export(entry.ProjectPath, Path.Combine(folder, "book.docx"), language == "ru", ct);
        // Include subsequent human review and current text in each new black-box export.
        foreach (var audit in Directory.EnumerateFiles(Path.Combine(entry.ProjectPath, "Import"), "review-*.json"))
        {
            var step = "human-review/" + Path.GetFileName(audit);
            if (!session.State.Artifacts.Any(a => a.Step == step)) session.AddBytes(step, File.ReadAllBytes(audit));
        }
        var chapters = new LiteraryChapterStore(entry.ProjectPath); chapters.Open();
        session.AddJson("export-text-snapshot", chapters.Snapshot());
        session.AddJson("result-manifest", new { entry.Id, book = "book.docx", blackBox = "import-history.xlsx",
            session.State.RagStatus, session.State.MemoryStatus, memoryRequiresApproval = status.Pending>0 || status.MissingMemory>0 || status.Excluded>0, preparation = status,
            review = JsonSerializer.Deserialize<ImportReviewFile>(File.ReadAllText(Path.Combine(entry.ProjectPath, "Import", "review.json")), ImportJson.Options) });
        session.State.Stage = session.State.RagStatus.StartsWith("ready", StringComparison.Ordinal)
            && session.State.MemoryStatus.StartsWith("proposals-ready", StringComparison.Ordinal) ? "complete" : "partial-result"; session.Save();
        try { ImportBlackBox.Export(session, Path.Combine(folder, "import-history.xlsx"), ct); }
        catch { session.State.Stage = "export"; session.Save(); throw; }
    }
    private static string CurrentRevision(string root)
    {
        var chapters = new LiteraryChapterStore(root); chapters.Open();
        return ImportSession.Hash(ImportSession.HashFile(Path.Combine(root, "Import", "review.json"))
            + JsonSerializer.Serialize(chapters.Snapshot(), ImportJson.Options));
    }
}
