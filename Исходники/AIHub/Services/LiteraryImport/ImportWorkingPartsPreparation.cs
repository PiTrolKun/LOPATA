using System.IO;
using System.Text.Json;

namespace AIHub.Services.LiteraryImport;

public sealed record ImportWorkingPartsManifest(int Version, string ProjectId, string BookRevision, string Generation,
    string Mode, string Collection, string VectorsHash, ImportWorkingPart[] Parts, Dictionary<string, string> Hashes);
public sealed record ImportWorkingPartsOutcome(bool Ready, ImportWorkingPartsPlan? Plan = null, string? Report = null);
public sealed record ImportWorkingPartsProgress(string Stage, int Done, int Total, int Attempt);

/// <summary>Publishes immutable working copies. Legacy import chapters remain recovery/editor inputs.</summary>
public sealed class ImportWorkingPartsPreparation(ImportSession session)
{
    private string Root => session.State.ProjectPath;
    private string DraftPath => Path.Combine(Root, "Import/working-parts-draft.json");
    private string ManifestPath => Path.Combine(Root, "WorkingParts/manifest.json");
    private static string Encode<T>(T value) => JsonSerializer.Serialize(value, ImportJson.Options);

    public ImportWorkingPartsPlan? LoadDraft()
    {
        if (!File.Exists(DraftPath)) return null;
        var plan = JsonSerializer.Deserialize<ImportWorkingPartsPlan>(LiteraryChapterFiles.Read(DraftPath), ImportJson.Options)
            ?? throw new InvalidDataException("Invalid draft.");
        plan.Validate(new ImportReviewBookStore(Root).Load()); return plan;
    }
    public void SaveDraft(ImportWorkingPartsPlan plan)
    {
        var layout = new LiteraryProjectLayout(Root);
        using var lease = Lock(layout);
        using var bookLease = new ImportReviewBookStore(Root).AcquireEditor();
        plan.Validate(new ImportReviewBookStore(Root).Load());
        LiteraryChapterFiles.Write(DraftPath, Encode(plan));
    }

    public Task<ImportWorkingPartsOutcome> PrepareAsync(string mode, IProgress<ImportWorkingPartsProgress> progress, CancellationToken ct)
        => RunAsync(mode, null, progress, ct);
    public Task<ImportWorkingPartsOutcome> CommitAsync(ImportWorkingPartsPlan plan, IProgress<ImportWorkingPartsProgress> progress, CancellationToken ct)
        => RunAsync(plan.Mode, JsonSerializer.Deserialize<ImportWorkingPartsPlan>(Encode(plan), ImportJson.Options), progress, ct);

    private Task<ImportWorkingPartsOutcome> RunAsync(string mode, ImportWorkingPartsPlan? accepted,
        IProgress<ImportWorkingPartsProgress> progress, CancellationToken ct) => Task.Run(() =>
    {
        if (mode is not ("auto" or "manual")) throw new ArgumentException("Invalid preparation mode.");
        var errors = new List<object>();
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            ct.ThrowIfCancellationRequested(); progress.Report(new("Preparing", 0, 0, attempt));
            try
            {
                var layout = new LiteraryProjectLayout(Root);
                using var lease = Lock(layout);
                using var bookLease = new ImportReviewBookStore(Root).AcquireEditor();
                if (session.State.Stage != "book-confirmed" || session.State.RagStatus != "ready")
                    throw new InvalidOperationException("Confirmed book and RAG are required.");
                var book = new ImportReviewBookStore(Root).Load();
                var rag = ImportWorkingPartsRag.RequireCurrent(Root, book);
                var plan = accepted ?? ImportWorkingPartsPlan.Create(book, mode);
                plan.Validate(book); ct.ThrowIfCancellationRequested();
                if (accepted is null && mode == "manual")
                {
                    // Resume a matching draft instead of discarding user-adjusted boundaries.
                    if (File.Exists(DraftPath))
                    {
                        var old = JsonSerializer.Deserialize<ImportWorkingPartsPlan>(LiteraryChapterFiles.Read(DraftPath), ImportJson.Options);
                        if (old?.BookRevision == plan.BookRevision && old.Mode == mode) { old.Validate(book); plan = old; }
                    }
                    LiteraryChapterFiles.Write(DraftPath, Encode(plan));
                    Journal(layout, "review", mode, attempt, errors);
                    return new ImportWorkingPartsOutcome(false, plan);
                }
                Publish(layout, book, plan, rag.Manifest, rag.Vectors, progress, attempt, ct);
                Journal(layout, "ready", mode, attempt, errors);
                return new ImportWorkingPartsOutcome(true, plan);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                errors.Add(new { attempt, error = ImportRagPreparation.ErrorCode(ex), hresult = ex.HResult, time = DateTimeOffset.UtcNow });
                if (attempt < 4) progress.Report(new("Retry", 0, 0, attempt + 1));
            }
        }
        var failed = new LiteraryProjectLayout(Root);
        var folder = failed.EnsureFolder(Path.Combine("Diagnostics/ImportReports", Guid.NewGuid().ToString("N")));
        var report = Path.Combine(folder, "report.json");
        File.WriteAllText(report, Encode(new { version = 1, stage = "WorkingParts", mode,
            appVersion = typeof(ImportWorkingPartsPreparation).Assembly.GetName().Version?.ToString(),
            attempts = errors, issueUrl = ImportRagPreparation.IssuesUrl }));
        Journal(failed, "failed", mode, 4, errors);
        return new ImportWorkingPartsOutcome(false, accepted, report);
    }, ct);

    private void Publish(LiteraryProjectLayout layout, ImportReviewBook book, ImportWorkingPartsPlan plan,
        ImportRagManifest rag, string vectors, IProgress<ImportWorkingPartsProgress> progress, int attempt, CancellationToken ct)
    {
        var generation = ImportSession.Hash(Encode(new { plan.ProjectId, plan.BookRevision, plan.Mode, plan.Parts }));
        var folder = layout.EnsureFolder(Path.Combine("WorkingParts", generation));
        var hashes = new Dictionary<string, string>();
        void WriteOwned(string name, string text)
        {
            var path = Path.Combine(folder, name);
            if (File.Exists(path))
            {
                if (LiteraryChapterFiles.Read(path) != text) throw new IOException("Working copy changed externally.");
            }
            else LiteraryChapterFiles.Write(path, text);
            hashes[name] = ImportSession.HashFile(path);
        }
        foreach (var part in plan.Parts)
        {
            ct.ThrowIfCancellationRequested();
            WriteOwned(part.Id + ".txt", book.Text.Substring(part.Start, part.Length));
            progress.Report(new("Writing", part.Number, plan.Parts.Count, attempt));
        }
        var links = ImportWorkingPartsRag.Build(plan, book, vectors, rag.Points, ct);
        WriteOwned("rag-links.json", Encode(links));
        WriteOwned("index.json", Encode(plan.Parts));
        var manifest = new ImportWorkingPartsManifest(1, book.ProjectId, plan.BookRevision, generation, plan.Mode,
            rag.Collection, rag.VectorsHash, plan.Parts.ToArray(), hashes);
        ct.ThrowIfCancellationRequested(); layout.EnsurePresent();
        if (new ImportReviewBookStore(Root).Load().Text != book.Text || ImportWorkingPartsRag.RequireCurrent(Root, book).Manifest != rag)
            throw new IOException("Book or RAG changed during preparation.");
        // The pointer is the single commit point. A cancelled/incomplete generation remains unreferenced.
        LiteraryChapterFiles.Write(ManifestPath, Encode(manifest));
        LiteraryChapterFiles.Write(DraftPath, Encode(plan));
        progress.Report(new("Ready", plan.Parts.Count, plan.Parts.Count, attempt));
    }

    public ImportWorkingPartsManifest? Current()
    {
        if (!File.Exists(ManifestPath)) return null;
        var book = new ImportReviewBookStore(Root).Load();
        var m = JsonSerializer.Deserialize<ImportWorkingPartsManifest>(LiteraryChapterFiles.Read(ManifestPath), ImportJson.Options)
            ?? throw new InvalidDataException("Invalid working parts manifest.");
        if (m.Version != 1 || m.ProjectId != book.ProjectId || m.BookRevision != ImportSession.Hash(book.Text)) return null;
        var plan = new ImportWorkingPartsPlan { ProjectId = m.ProjectId, BookRevision = m.BookRevision, Mode = m.Mode, Parts = m.Parts.ToList() };
        plan.Validate(book);
        var generation = ImportSession.Hash(Encode(new { plan.ProjectId, plan.BookRevision, plan.Mode, plan.Parts }));
        if (m.Generation != generation) throw new InvalidDataException("Invalid working generation.");
        var rag = ImportWorkingPartsRag.RequireCurrent(Root, book);
        if (m.Collection != rag.Manifest.Collection || m.VectorsHash != rag.Manifest.VectorsHash) return null;
        var folder = Path.Combine(Root, "WorkingParts", generation);
        var expected = plan.Parts.Select(p => p.Id + ".txt").Append("rag-links.json").Append("index.json").ToHashSet();
        if (!expected.SetEquals(m.Hashes.Keys)) throw new InvalidDataException("Incomplete working generation.");
        foreach (var (name, hash) in m.Hashes)
            if (ImportSession.HashFile(Path.Combine(folder, name)) != hash) throw new InvalidDataException("Working file checksum differs.");
        foreach (var part in plan.Parts)
            if (LiteraryChapterFiles.Read(Path.Combine(folder, part.Id + ".txt")) != book.Text.Substring(part.Start, part.Length))
                throw new InvalidDataException("Working file differs from book.");
        return m;
    }

    private static FileStream Lock(LiteraryProjectLayout layout) => new(Path.Combine(layout.EnsureFolder("Import"), "working-parts.lock"),
        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    private static void Journal(LiteraryProjectLayout layout, string status, string mode, int attempt, List<object> errors)
        => LiteraryChapterFiles.Write(Path.Combine(layout.EnsureFolder("Import"), "working-parts-state.json"),
            Encode(new { version = 1, status, mode, attempt, attempts = errors, time = DateTimeOffset.UtcNow }));
}
