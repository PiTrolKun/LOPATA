using System.IO;
using System.Text.Json;

namespace AIHub.Services.LiteraryImport;

public sealed record ImportRagProgress(string Area, string Stage, double Percent, long Characters, int Attempt);
public sealed record ImportRagManifest(string Area, string Revision, string Collection, int Points,
    string ModelRevision, string InputHash, string VectorsHash, long Characters);
public sealed record ImportRagOutcome(bool Ready, string? Report = null);

/// <summary>Only reference + confirmed-book RAG. It cannot create working parts or Jelly.</summary>
public sealed class ImportRagPreparation(ImportSession session, ImportPreparationAnswers answers,
    IImportRagBackend? backend = null)
{
    public static string IssuesUrl => "https://github.com/" + ApplicationUpdateService.Repository + "/issues";
    private readonly List<object> _attempts = [];

    public async Task<ImportRagOutcome> RunAsync(IProgress<ImportRagProgress> progress, CancellationToken ct)
    {
        if (session.State.Stage != "book-confirmed" || !new ImportPostReviewQuestions(answers).Complete)
            throw new InvalidOperationException("Book and answers must be confirmed first.");
        var layout = new LiteraryProjectLayout(session.State.ProjectPath);
        using var lease = new FileStream(Path.Combine(layout.EnsureFolder("Import"), "rag-preparation.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var bookLease = new ImportReviewBookStore(layout.Root).AcquireEditor();
        if (backend is null)
            await ComponentLicenseGate.EnsureAsync([GigaEmbeddingInstallation.LicenseId,
                GigaEmbeddingInstallation.RuntimeLicenseId, QdrantOptions.LicenseId], ct);
        await using var worker = backend ?? new ImportRagBackend(layout);
        session.State.RagStatus = "pending"; session.Save();
        var basis = answers.Values.GetValueOrDefault("9", "").Trim();
        var paths = answers.Values.GetValueOrDefault("10", "").Split('|', StringSplitOptions.RemoveEmptyEntries);
        if (basis.StartsWith("нет", StringComparison.OrdinalIgnoreCase) || basis.StartsWith("no", StringComparison.OrdinalIgnoreCase)) paths = [];
        foreach (var area in new[] { "Reference", "Book" })
        {
            if (area == "Reference" && paths.Length == 0)
            {
                // A skipped stage is explicit and does not manufacture an empty index.
                progress.Report(new(area, "Skipped", 100, 0, 0));
                Journal(layout, area, "skipped", 0); continue;
            }
            var succeeded = false;
            for (var attempt = 1; attempt <= 4; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                progress.Report(new(area, "Reading", -1, 0, attempt));
                try
                {
                    var input = await Task.Run(() => area == "Reference"
                        ? ImportRagInput.Reference(layout, paths, ct) : ImportRagInput.Book(layout.Root), ct);
                    var folder = layout.EnsureFolder(Path.Combine("Rag/ImportIndexes", area, input.Revision));
                    var inputFile = Path.Combine(folder, "text.json");
                    var vectors = Path.Combine(folder, "vectors.jsonl");
                    var manifestFile = Path.Combine(folder, "manifest.json");
                    var bridge = new InlineProgress<LiteraryPreparationProgress>(p =>
                        progress.Report(new(area, p.Stage, p.Percent, input.Characters, attempt)));
                    progress.Report(new(area, "Checking", -1, input.Characters, attempt));
                    var manifest = ReadManifest(manifestFile);
                    var reusable = manifest is not null && manifest.Area == area && manifest.Revision == input.Revision
                        && manifest.ModelRevision == GigaEmbeddingInstallation.Revision
                        && Matches(inputFile, manifest.InputHash) && Matches(vectors, manifest.VectorsHash);
                    if (reusable)
                    {
                        try { reusable = await worker.VerifyAsync(manifest!.Collection, vectors, manifest.Points, ct); }
                        catch (Exception) when (!ct.IsCancellationRequested) { reusable = false; }
                    }
                    if (!reusable)
                    {
                        LiteraryChapterFiles.Write(inputFile, JsonSerializer.Serialize(input.Sections));
                        var id = ImportSession.Hash(layout.ProjectId + area + input.Revision + GigaEmbeddingInstallation.Revision)[..32].ToLowerInvariant();
                        Journal(layout, area, "running", attempt);
                        var points = await worker.BuildAsync(id, inputFile, vectors, bridge, ct);
                        if (points <= 0) throw new InvalidDataException("Empty index.");
                        manifest = new(area, input.Revision, id, points, GigaEmbeddingInstallation.Revision,
                            ImportSession.HashFile(inputFile), ImportSession.HashFile(vectors), input.Characters);
                    }
                    ct.ThrowIfCancellationRequested(); layout.EnsurePresent();
                    ImportRagArtifact.Validate(vectors, input, manifest!.Points, ct);
                    if (area == "Book" && ImportRagInput.Book(layout.Root).Revision != input.Revision)
                        throw new IOException("Book changed during indexing.");
                    if (area == "Reference" && paths.Where((p, i) => File.Exists(p) && ImportSession.HashFile(p) != input.Sources[i].Sha256).Any())
                        throw new IOException("Reference changed during indexing.");
                    // Durable manifest follows vector import and source checks, never precedes them.
                    LiteraryChapterFiles.Write(manifestFile, JsonSerializer.Serialize(manifest));
                    Publish(layout, input, manifest, folder);
                    Journal(layout, area, "ready", attempt);
                    progress.Report(new(area, "Ready", 100, input.Characters, attempt));
                    succeeded = true; break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // No exception messages: providers may echo private text, paths or credentials.
                    _attempts.Add(new { area, attempt, error = ErrorCode(ex), hresult = ex.HResult, time = DateTimeOffset.UtcNow });
                    Journal(layout, area, "failed", attempt);
                    if (attempt < 4) { progress.Report(new(area, "Retry", -1, 0, attempt + 1)); continue; }
                }
            }
            if (!succeeded)
            {
                session.State.RagStatus = "failed"; session.Save();
                return new(false, WriteReport(layout, area));
            }
        }
        session.State.RagStatus = "ready"; session.Save();
        Journal(layout, "Both", "ready", 0);
        return new(true);
    }

    private static ImportRagManifest? ReadManifest(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<ImportRagManifest>(LiteraryChapterFiles.Read(path)) : null; }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }
    private static bool Matches(string path, string hash) => File.Exists(path) && ImportSession.HashFile(path) == hash;

    private static void Publish(LiteraryProjectLayout layout, ImportRagInput input, ImportRagManifest manifest, string folder)
    {
        if (input.Area == "Book")
        {
            var target = layout.EnsureFolder("Rag/Book");
            LiteraryChapterFiles.Write(Path.Combine(target, "manifest.json"), JsonSerializer.Serialize(manifest));
            return;
        }
        var sourceFolder = layout.EnsureFolder("Rag/Source");
        var previousHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var hashFile = Path.Combine(sourceFolder, "sources.json");
        if (File.Exists(hashFile))
        {
            using var previous = JsonDocument.Parse(LiteraryChapterFiles.Read(hashFile)); var n = 0;
            foreach (var source in previous.RootElement.EnumerateArray())
                previousHashes[$"{++n:D4}_" + source.GetProperty("file").GetString()] = source.GetProperty("sha256").GetString()!;
        }
        var guard = Path.Combine(sourceFolder, "editing.json");
        if (File.Exists(guard) && File.ReadAllText(guard) != "{\"importPreparation\":true}")
            throw new IOException("Reference editor has an unfinished transaction.");
        // The existing reader refuses a partially published reference area.
        LiteraryChapterFiles.Write(guard, "{\"importPreparation\":true}");
        var materials = new List<string>();
        for (var i = 0; i < input.Sources.Length; i++)
        {
            var source = input.Sources[i];
            var relative = Path.Combine("Materials", $"{i + 1:D4}_" + source.File);
            layout.EnsureFolder("Materials");
            var destination = Path.Combine(layout.Root, relative);
            if (File.Exists(destination) && ImportSession.HashFile(destination) != source.Sha256)
            {
                if (!previousHashes.TryGetValue(Path.GetFileName(destination), out var previousHash)
                    || ImportSession.HashFile(destination) != previousHash)
                    throw new IOException("Existing material was edited; not overwriting it.");
                File.Copy(Path.Combine(layout.Root, source.Snapshot), destination + ".import-tmp", true);
                File.Replace(destination + ".import-tmp", destination, destination + ".bak");
            }
            if (!File.Exists(destination)) File.Copy(Path.Combine(layout.Root, source.Snapshot), destination);
            materials.Add(relative);
        }
        foreach (var name in new[] { "text.json", "vectors.jsonl" })
        {
            var destination = Path.Combine(sourceFolder, name);
            File.Copy(Path.Combine(folder, name), destination + ".import-tmp", true);
            File.Move(destination + ".import-tmp", destination, true);
        }
        LiteraryChapterFiles.Write(Path.Combine(sourceFolder, "sources.json"), JsonSerializer.Serialize(input.Sources.Select(s => new { file = s.File, sha256 = s.Sha256 })));
        LiteraryChapterFiles.Write(Path.Combine(sourceFolder, "manifest.json"), JsonSerializer.Serialize(
            new LiterarySourceIndex.Manifest(manifest.Collection, manifest.ModelRevision, 1024, manifest.Points, "reference")));
        var project = LiteraryProjectStore.ReadProject(layout.Root);
        project.Materials = materials;
        LiteraryChapterFiles.Write(Path.Combine(layout.Root, "project.json"), JsonSerializer.Serialize(project, new JsonSerializerOptions { WriteIndented = true }));
        File.Delete(guard);
    }

    private void Journal(LiteraryProjectLayout layout, string area, string status, int attempt)
    {
        LiteraryChapterFiles.Write(Path.Combine(layout.EnsureFolder("Import"), "rag-preparation.json"),
            JsonSerializer.Serialize(new { version = 1, area, status, attempt, time = DateTimeOffset.UtcNow, attempts = _attempts }));
    }
    public static string ErrorCode(Exception error) => error switch
    {
        UnauthorizedAccessException => "access-denied", InvalidDataException => "invalid-data",
        IOException => "file-or-storage", LiteraryEmbeddingException => "embedding-worker",
        System.Net.Http.HttpRequestException => "local-service", OperationCanceledException => "timeout",
        JsonException => "invalid-json", _ => "preparation-failed"
    };
    private string WriteReport(LiteraryProjectLayout layout, string area)
    {
        var folder = layout.EnsureFolder(Path.Combine("Diagnostics/ImportReports", Guid.NewGuid().ToString("N")));
        var path = Path.Combine(folder, "report.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { version = 1,
            appVersion = typeof(ImportRagPreparation).Assembly.GetName().Version?.ToString(),
            stage = area, attempts = _attempts, issueUrl = IssuesUrl }, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }
}
