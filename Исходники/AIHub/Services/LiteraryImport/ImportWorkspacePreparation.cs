using System.IO;
using System.Text.Json;

namespace AIHub.Services.LiteraryImport;

public sealed record ImportWorkspaceState(int Version, string ProjectId, string Generation, string Choice,
    string Activation, string PreviousTransaction, Dictionary<string, string> PreviousHashes,
    LiteraryChapterIndex Index, string ProjectBefore, string ProjectAfter, bool Ready = false);
public sealed record ImportWorkspaceOutcome(bool Ready, string? Report = null);

/// <summary>Recoverable activation. No model inference, manuscript rewriting or repeated blank drafts.</summary>
public sealed class ImportWorkspacePreparation(ImportSession session, ImportPreparationAnswers answers,
    Func<string, string> localize, Func<string, string, CancellationToken, Task<int>>? publish = null)
{
    private string Root => session.State.ProjectPath;
    private string StatePath => Path.Combine(Root, "Import/workspace.json");
    private static string Encode<T>(T value) => JsonSerializer.Serialize(value, ImportJson.Options);
    public ImportWorkspaceState? Load()
    {
        if (!File.Exists(StatePath)) return null;
        var state = JsonSerializer.Deserialize<ImportWorkspaceState>(LiteraryChapterFiles.Read(StatePath), ImportJson.Options);
        if (state is null || state.Version != 1 || state.ProjectId != session.State.ProjectId
            || state.Choice is not ("last" or "new") || !Guid.TryParseExact(state.Activation, "N", out _))
            throw new InvalidDataException("Invalid workspace activation.");
        return state;
    }
    public Task<ImportWorkspaceOutcome> RunAsync(string choice, IProgress<ImportWorkingPartsProgress> progress, CancellationToken ct)
        => Task.Run(async () =>
    {
        if (choice is not ("last" or "new")) throw new ArgumentException("Invalid initial fragment.");
        var errors = new List<object>(); var layout = new LiteraryProjectLayout(Root);
        QdrantRuntime? runtime = null;
        try
        {
            for (var attempt = 1; attempt <= 4; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var jellyLock = Lock("jelly.lock"); using var partsLock = Lock("working-parts.lock");
                    using var bookLock = new ImportReviewBookStore(Root).AcquireEditor();
                    layout.EnsurePresent();
                    var state = Load();
                    if (state?.Ready == true)
                    {
                        // Never replace a draft or metadata that the user has edited after entry.
                        var existing = new LiteraryChapterStore(Root); existing.Open();
                        session.State.Stage = "workspace-ready"; session.Save();
                        return new ImportWorkspaceOutcome(true);
                    }
                    if (session.State.Stage != "book-confirmed" || session.State.RagStatus != "ready" || session.State.MemoryStatus != "ready"
                        || !new ImportPostReviewQuestions(answers).Complete)
                        throw new InvalidOperationException("Preparation is incomplete.");
                    var parts = new ImportWorkingPartsPreparation(session).Current() ?? throw new InvalidDataException("Working parts are stale.");
                    var executor = LiteraryProjectStore.ReadProject(Root).JellyExecutor;
                    var memoryState = new ImportJellyPreparation(session, executor).LoadState();
                    if (memoryState?.Status != "ready" || memoryState.Done != parts.Parts.Length) throw new InvalidDataException("Memory is incomplete.");
                    var memory = new LiteraryJellyStore(layout);
                    foreach (var part in parts.Parts)
                    {
                        var revision = LiteraryWorkIndex.Revision(LiteraryChapterFiles.Read(Path.Combine(Root, "WorkingParts", parts.Generation, part.Id + ".txt")));
                        var batch = memory.Find(part.Id, revision);
                        if (batch?.Status != "confirmed" || batch.ImportGeneration != parts.Generation) throw new InvalidDataException("Memory source is incomplete.");
                    }
                    var chapters = new LiteraryChapterStore(Root); chapters.Open();
                    state ??= Create(parts, chapters, choice);
                    if (state.Generation != parts.Generation) throw new IOException("Literary.Import.Workspace.SourceChanged");
                    LiteraryChapterFiles.Write(StatePath, Encode(state));
                    var done = 0;
                    foreach (var (manifest, vectors) in ImportWorkspaceIndexes.Build(Root, parts, state.Index))
                    {
                        ct.ThrowIfCancellationRequested();
                        progress.Report(new("Preparing", done, parts.Parts.Length, attempt));
                        var folder = layout.EnsureFolder(Path.Combine("Rag/Work", manifest.PartId));
                        var path = Path.Combine(folder, "manifest.json");
                        var old = File.Exists(path) ? JsonSerializer.Deserialize<LiteraryWorkManifest>(LiteraryChapterFiles.Read(path)) : null;
                        if (old != manifest)
                        {
                            var file = Path.Combine(folder, "import-vectors.jsonl"); LiteraryChapterFiles.Write(file, vectors);
                            int count;
                            if (publish is not null) count = await publish(manifest.Collection, file, ct);
                            else
                            {
                                runtime ??= layout.CreateRuntime();
                                count = await LiteraryRagImport.ReplaceAsync(runtime, manifest.Collection, file,
                                    new InlineProgress<LiteraryPreparationProgress>(_ => { }), ct);
                            }
                            if (count != manifest.Points) throw new InvalidDataException("Working index count differs.");
                            LiteraryChapterFiles.Write(path, Encode(manifest));
                        }
                        progress.Report(new("Preparing", ++done, parts.Parts.Length, attempt));
                    }
                    ct.ThrowIfCancellationRequested();
                    if (new ImportWorkingPartsPreparation(session).Current()?.Generation != state.Generation)
                        throw new IOException("Literary.Import.Workspace.SourceChanged");
                    var texts = state.Index.Parts.ToDictionary(p => p.FileName, p => parts.Parts.Any(x => x.Id == p.Id)
                        ? LiteraryChapterFiles.Read(Path.Combine(Root, "WorkingParts", parts.Generation, p.Id + ".txt")) : "");
                    // No cancellation between the chapter commit and its completion marker.
                    chapters.ActivateImport(state.Index, texts, state.PreviousTransaction, state.PreviousHashes, state.Activation);
                    var projectPath = Path.Combine(Root, "project.json"); var actualProject = LiteraryChapterFiles.Read(projectPath);
                    if (actualProject != state.ProjectBefore && actualProject != state.ProjectAfter) throw new IOException("Project settings changed.");
                    if (actualProject != state.ProjectAfter) LiteraryChapterFiles.Write(projectPath, state.ProjectAfter);
                    LiteraryChapterFiles.Write(StatePath, Encode(state with { Ready = true }));
                    session.State.Stage = "workspace-ready"; session.Save();
                    return new ImportWorkspaceOutcome(true);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    errors.Add(new { attempt, error = ImportRagPreparation.ErrorCode(ex), hresult = ex.HResult, time = DateTimeOffset.UtcNow });
                }
            }
        }
        finally { if (runtime is not null) await runtime.StopAsync(); }
        var report = Path.Combine(layout.EnsureFolder(Path.Combine("Diagnostics/ImportReports", Guid.NewGuid().ToString("N"))), "report.json");
        LiteraryChapterFiles.Write(report, Encode(new { version = 1, stage = "Workspace", attempts = errors,
            appVersion = typeof(ImportWorkspacePreparation).Assembly.GetName().Version?.ToString(), issueUrl = ImportRagPreparation.IssuesUrl }));
        return new ImportWorkspaceOutcome(false, report);
    }, ct);

    private ImportWorkspaceState Create(ImportWorkingPartsManifest parts, LiteraryChapterStore chapters, string choice)
    {
        if (!string.IsNullOrWhiteSpace(chapters.Load())) throw new IOException("Literary.Import.Workspace.ExistingDraft");
        var index = new LiteraryChapterIndex { AutosaveSeconds = chapters.Index.AutosaveSeconds };
        foreach (var group in parts.Parts.GroupBy(p => p.Chapter))
        {
            var n = 0; var title = LiteraryChapterFiles.SafeTitle(group.First().Title);
            foreach (var part in group)
                index.Parts.Add(new() { Id = part.Id, Chapter = part.Chapter, Part = ++n, Title = title,
                    FileName = LiteraryChapterFiles.FileName(part.Chapter, n, title), Finished = true, ExactContinuation = true });
        }
        if (choice == "new")
        {
            var title = localize("Literary.Import.Workspace.NewTitle"); var number = index.Parts.Max(p => p.Chapter) + 1;
            index.Parts.Add(new() { Chapter = number, Title = title, FileName = LiteraryChapterFiles.FileName(number, 1, title) });
        }
        index.ActiveId = index.Parts[^1].Id; index.Parts[^1].Finished = false;
        return new(1, parts.ProjectId, parts.Generation, choice, Guid.NewGuid().ToString("N"), chapters.Index.Transaction,
            chapters.Index.Parts.ToDictionary(p => p.FileName, p => ImportSession.HashFile(Path.Combine(Root, "chapters", p.FileName))),
            index, LiteraryChapterFiles.Read(Path.Combine(Root, "project.json")), ImportWorkspaceProject.Build(Root, answers, localize));
    }
    private FileStream Lock(string name) => new(Path.Combine(Root, "Import", name), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
}
