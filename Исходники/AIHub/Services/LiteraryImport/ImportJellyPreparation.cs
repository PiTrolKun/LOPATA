using System.Globalization;
using System.IO;
using System.Text.Json;

namespace AIHub.Services.LiteraryImport;

public sealed record ImportJellyState(int Version, string Generation, string Executor, string Mode,
    string Status, int Done, int Total, DateTimeOffset Time);
public sealed record ImportJellyOutcome(bool Ready, LiteraryJellyBatch? Review = null, string? Report = null);
public sealed record ImportJellyProgress(string Stage, int Done, int Total, int Attempt);

/// <summary>Resumable memory extraction from committed working parts, independent of their preparation mode.</summary>
public sealed class ImportJellyPreparation(ImportSession session, string executor)
{
    private string Root => session.State.ProjectPath;
    private string StatePath => Path.Combine(Root, "Import/jelly-state.json");
    private static string Encode<T>(T value) => JsonSerializer.Serialize(value, ImportJson.Options);
    private LiteraryProjectLayout Layout => new(Root);

    public ImportJellyState? LoadState()
    {
        if (!File.Exists(StatePath)) return null;
        var state = JsonSerializer.Deserialize<ImportJellyState>(LiteraryChapterFiles.Read(StatePath), ImportJson.Options);
        var current = new ImportWorkingPartsPreparation(session).Current();
        return state is { Version: 1, Mode: "auto" or "manual" } && current?.Generation == state.Generation
            && state.Executor == executor ? state : null;
    }

    public void SaveMode(string mode)
    {
        ValidateMode(mode);
        using var lease = Lock("jelly.lock");
        var current = new ImportWorkingPartsPreparation(session).Current() ?? throw new IOException("Working parts are not ready.");
        var previous = LoadState();
        SaveState(current, mode, previous?.Status ?? "pending", previous?.Done ?? 0);
    }

    public Task<ImportJellyOutcome> RunAsync(string mode, Func<string, CancellationToken, Task<string>> extract,
        IProgress<ImportJellyProgress> progress, CancellationToken ct) => Task.Run(async () =>
    {
        ValidateMode(mode); LiteraryJellyInstallation.ValidateMode(executor);
        var errors = new List<object>();
        ImportWorkingPartsManifest? current = null; var done = 0;
        try
        {
            return await Retry(async () =>
            {
                using var lease = Lock("jelly.lock");
                using var partsLease = Lock("working-parts.lock");
                using var bookLease = new ImportReviewBookStore(Root).AcquireEditor();
                if (session.State.Stage != "book-confirmed" || session.State.RagStatus != "ready")
                    throw new InvalidOperationException("Confirmed book and RAG are required.");
                current = new ImportWorkingPartsPreparation(session).Current() ?? throw new IOException("Working parts are not ready.");
                var memory = new LiteraryJellyStore(Layout);
                SaveState(current, mode, "running", 0);
                session.State.MemoryStatus = "pending"; session.Save();
                done = 0;
                foreach (var part in current.Parts)
                {
                    ct.ThrowIfCancellationRequested();
                    var text = LiteraryChapterFiles.Read(Path.Combine(Root, "WorkingParts", current.Generation, part.Id + ".txt"));
                    var revision = LiteraryWorkIndex.Revision(text);
                    var batch = memory.Find(part.Id, revision);
                    if (batch is not null && batch.ImportGeneration != current.Generation)
                        batch = memory.RebindImported(batch, current.Generation, part.Number.ToString(CultureInfo.InvariantCulture));
                    if (batch?.Status == "confirmed") { done++; continue; }
                    if (batch is null)
                    {
                        var chunks = LiteraryJellyContract.Chunks(text).ToArray();
                        var facts = new List<LiteraryJellyFact>();
                        var folder = Layout.EnsureFolder(Path.Combine("Jelly/ImportStaging", current.Generation, executor + "-v1", part.Id));
                        for (var index = 0; index < chunks.Length; index++)
                        {
                            ct.ThrowIfCancellationRequested();
                            progress.Report(new("Extracting", done, current.Parts.Length, 1));
                            var path = Path.Combine(folder, index.ToString("000", CultureInfo.InvariantCulture) + ".json");
                            LiteraryJellyFact[] rows;
                            if (File.Exists(path)) rows = JsonSerializer.Deserialize<LiteraryJellyFact[]>(LiteraryChapterFiles.Read(path))
                                ?? throw new InvalidDataException("Unreadable memory checkpoint.");
                            else
                            {
                                // Keep chunk indices stable so older checkpoints remain reusable.
                                // A standalone scene divider has no facts; the manuscript is unchanged.
                                rows = ImportJellyResponse.IsSeparator(chunks[index]) ? [] : await Extract(chunks[index]);
                                // Save the first parseable response before any later write can fail.
                                LiteraryChapterFiles.Write(path, Encode(rows));
                            }
                            facts.AddRange(rows);
                        }
                        batch = new(Guid.NewGuid().ToString("N"), part.Id, part.Number.ToString(CultureInfo.InvariantCulture),
                            revision, text, facts.ToArray(), ImportGeneration: current.Generation);
                        ct.ThrowIfCancellationRequested();
                        memory.Stage(batch);
                    }
                    ct.ThrowIfCancellationRequested();
                    if (mode == "manual" && batch.Facts.Length > 0)
                    {
                        SaveState(current, mode, "review", done);
                        return new ImportJellyOutcome(false, ReadReviewDraft(batch));
                    }
                    memory.ConfirmImported(batch, batch.Facts, mode);
                    done++; SaveState(current, mode, "running", done);
                    progress.Report(new("Extracting", done, current.Parts.Length, 1));
                }
                // Full validation is repeated before publishing completion.
                if (new ImportWorkingPartsPreparation(session).Current()?.Generation != current.Generation)
                    throw new IOException("Working parts changed during memory extraction.");
                ct.ThrowIfCancellationRequested();
                SaveState(current, mode, "ready", done);
                session.State.MemoryStatus = "ready"; session.Save();
                progress.Report(new("Ready", done, current.Parts.Length, 1));
                return new ImportJellyOutcome(true);
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (current is not null) SaveState(current, mode, "stopped", done);
            throw;
        }

        async Task<LiteraryJellyFact[]> Extract(string text)
        {
            // One retry budget for the failing request. Outer retries must not multiply it.
            for (var attempt = 1; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try { return ImportJellyResponse.Parse(await extract(text, ct)); }
                catch (Exception ex) when (!ct.IsCancellationRequested && ex is not OutOfMemoryException)
                {
                    errors.Add(Failure(attempt, ex));
                    if (attempt == 4) throw new ImportJellyExhaustedException();
                    progress.Report(new("Retry", done, current!.Parts.Length, attempt + 1));
                }
            }
        }
        async Task<ImportJellyOutcome> Retry(Func<Task<ImportJellyOutcome>> action)
        {
            for (var attempt = 1; attempt <= 4; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try { return await action(); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    if (ex is not ImportJellyExhaustedException) errors.Add(Failure(attempt, ex));
                    if (ex is ImportJellyExhaustedException || attempt == 4) break;
                    progress.Report(new("Retry", done, current?.Parts.Length ?? 0, attempt + 1));
                }
            }
            try { if (current is not null) SaveState(current, mode, "failed", done); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { errors.Add(Failure(4, ex)); }
            return new(false, Report: Report(mode, errors));
        }
    }, ct);

    public void SaveReviewDraft(LiteraryJellyBatch batch, IReadOnlyList<LiteraryJellyFact> decisions)
    {
        ValidateDecisions(batch, decisions);
        // Saving a draft does not confirm facts or modify the immutable model proposal.
        LiteraryChapterFiles.Write(ReviewPath(batch), Encode(batch with { Facts = decisions.ToArray() }));
    }

    public LiteraryJellyBatch ReadReviewDraft(LiteraryJellyBatch batch)
    {
        var path = ReviewPath(batch);
        if (!File.Exists(path)) return batch;
        var draft = JsonSerializer.Deserialize<LiteraryJellyBatch>(LiteraryChapterFiles.Read(path), ImportJson.Options)
            ?? throw new InvalidDataException("Unreadable review draft.");
        if (draft.Id != batch.Id || draft.SourceText != batch.SourceText || draft.Revision != batch.Revision || draft.PartId != batch.PartId)
            throw new InvalidDataException("Review draft source changed.");
        ValidateDecisions(batch, draft.Facts); return batch with { Facts = draft.Facts };
    }

    public Task<string?> ConfirmAsync(LiteraryJellyBatch draft, IReadOnlyList<LiteraryJellyFact> decisions, bool warnings, CancellationToken ct)
        => Task.Run(() =>
    {
        var errors = new List<object>();
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var lease = Lock("jelly.lock"); using var parts = Lock("working-parts.lock");
                using var book = new ImportReviewBookStore(Root).AcquireEditor();
                var memory = new LiteraryJellyStore(Layout);
                var original = memory.Find(draft.PartId, draft.Revision) ?? throw new IOException("Missing memory proposal.");
                if (original.Id != draft.Id) throw new IOException("Memory proposal changed.");
                ValidateDecisions(original, decisions);
                if (original.Status != "confirmed") memory.ConfirmImported(original, decisions, "manual", warnings);
                return null;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is not OutOfMemoryException) { errors.Add(Failure(attempt, ex)); }
        }
        return Report("manual", errors);
    }, ct);

    private string ReviewPath(LiteraryJellyBatch batch)
    {
        if (!Guid.TryParseExact(batch.Id, "N", out _)) throw new InvalidDataException("Invalid batch identity.");
        return Path.Combine(Layout.EnsureFolder("Jelly/ImportReview"), batch.Id + ".json");
    }
    private static void ValidateDecisions(LiteraryJellyBatch batch, IReadOnlyList<LiteraryJellyFact> decisions)
    {
        if (decisions.Count != batch.Facts.Length || decisions.Select(f => f.Id).Distinct().Count() != decisions.Count
            || !decisions.Select(f => f.Id).Order().SequenceEqual(batch.Facts.Select(f => f.Id).Order()))
            throw new InvalidDataException("Each fact needs one decision.");
    }
    private static void ValidateMode(string mode)
    { if (mode is not ("auto" or "manual")) throw new ArgumentException("Invalid memory mode."); }
    private FileStream Lock(string name) => new(Path.Combine(Layout.EnsureFolder("Import"), name), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    private void SaveState(ImportWorkingPartsManifest m, string mode, string status, int done) => LiteraryChapterFiles.Write(StatePath,
        Encode(new ImportJellyState(1, m.Generation, executor, mode, status, done, m.Parts.Length, DateTimeOffset.UtcNow)));
    private static object Failure(int attempt, Exception ex) => new { attempt, error = ImportRagPreparation.ErrorCode(ex), hresult = ex.HResult, time = DateTimeOffset.UtcNow };
    private string Report(string mode, List<object> errors)
    {
        var path = Path.Combine(Layout.EnsureFolder(Path.Combine("Diagnostics/ImportReports", Guid.NewGuid().ToString("N"))), "report.json");
        LiteraryChapterFiles.Write(path, Encode(new { version = 1, stage = "Jelly", mode, executor,
            appVersion = typeof(ImportJellyPreparation).Assembly.GetName().Version?.ToString(), attempts = errors, issueUrl = ImportRagPreparation.IssuesUrl }));
        return path;
    }
    private sealed class ImportJellyExhaustedException : IOException;
}
