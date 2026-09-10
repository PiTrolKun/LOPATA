using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace AIHub.Services;

/// <summary>A temporary index owns its source snapshot and collection until project commit.</summary>
public sealed class LiterarySourceIndex : IAsyncDisposable
{
    public static string StagingRoot => Path.Combine(AppDataPaths.BaseDirectory, "Literary", "RagStaging");
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string DirectoryPath { get; }
    public List<string> Sources { get; } = [];
    public bool Ready { get; private set; }
    public int PointCount { get; private set; }
    private readonly FileStream _lease;
    private readonly QdrantRuntime _runtime;
    private readonly ILiterarySourceEmbedding _embedding;
    private readonly string _stagingRoot;
    private bool _committed, _disposed, _collectionAttempted;
    private string Marker => Path.Combine(DirectoryPath, "pending.json");
    public sealed record Pending(string Id, string? ProjectPath);
    public sealed record Manifest(string Id, string ModelRevision, int Dimension, int Points, string Kind);

    public LiterarySourceIndex(QdrantRuntime? runtime = null, ILiterarySourceEmbedding? embedding = null, string? stagingRoot = null)
    {
        _runtime = runtime ?? QdrantRuntime.Shared;
        _embedding = embedding ?? new GigaSourceEmbedding();
        _stagingRoot = Path.GetFullPath(stagingRoot ?? StagingRoot);
        DirectoryPath = Path.Combine(_stagingRoot, Id); Directory.CreateDirectory(DirectoryPath);
        _lease = new FileStream(Path.Combine(DirectoryPath, "owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        File.WriteAllText(Marker, JsonSerializer.Serialize(new Pending(Id, null)));
    }
    public async Task PrepareAsync(IReadOnlyList<string> paths, IProgress<LiteraryPreparationProgress> progress, CancellationToken ct)
    {
        await ComponentLicenseGate.EnsureAsync([GigaEmbeddingInstallation.LicenseId, GigaEmbeddingInstallation.RuntimeLicenseId, QdrantOptions.LicenseId], ct);
        var sections = new List<LiterarySourceSection>();
        var hashes = new List<object>();
        for (var i = 0; i < paths.Count; i++)
        {
            progress.Report(new("Reading", 100.0 * i / paths.Count, Path.GetFileName(paths[i])));
            var folder = Path.Combine(DirectoryPath, "sources", i.ToString("D4")); Directory.CreateDirectory(folder);
            var snapshot = Path.Combine(folder, Path.GetFileName(paths[i]));
            await using (var input = File.OpenRead(paths[i]))
            await using (var output = File.Create(snapshot)) await input.CopyToAsync(output, ct);
            Sources.Add(snapshot);
            sections.AddRange(await Task.Run(() => LiterarySourceReader.Read(snapshot, $"{i + 1:D4}_" + Path.GetFileName(snapshot), ct), ct));
            if (sections.Sum(s => (long)s.Text.Length) > LiterarySourceReader.MaxCharacters) throw new InvalidDataException("Combined source text exceeds 20 million characters.");
            await using var file = File.OpenRead(snapshot);
            hashes.Add(new { file = Path.GetFileName(snapshot), sha256 = Convert.ToHexString(await SHA256.HashDataAsync(file, ct)) });
        }
        var inputPath = Path.Combine(DirectoryPath, "text.json");
        await File.WriteAllTextAsync(inputPath, JsonSerializer.Serialize(sections), ct);
        await File.WriteAllTextAsync(Path.Combine(DirectoryPath, "sources.json"), JsonSerializer.Serialize(hashes), ct);
        var vectorsPath = Path.Combine(DirectoryPath, "vectors.jsonl");
        await _embedding.EmbedAsync(inputPath, vectorsPath, progress, ct);
        // The worker has exited; all GPU allocations are released before Qdrant import.
        _collectionAttempted = true;
        await _runtime.CreateLiteraryIndexAsync(Id, ct);
        var batch = new List<JsonElement>();
        using (var reader = File.OpenText(vectorsPath))
        {
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                using var doc = JsonDocument.Parse(line);
                ValidatePoint(doc.RootElement);
                batch.Add(doc.RootElement.Clone()); PointCount++;
                if (batch.Count == 32) { await FlushAsync(); }
            }
            if (batch.Count > 0) await FlushAsync();
        }
        async Task FlushAsync()
        {
            await _runtime.WriteLiteraryPointsAsync(Id, batch, ct);
            progress.Report(new("Writing", -1, PointCount.ToString())); batch.Clear();
        }
        if (PointCount == 0 || await _runtime.LiteraryPointCountAsync(Id, ct) != PointCount)
            throw new InvalidDataException("Qdrant point count mismatch.");
        using (var firstLine = File.OpenText(vectorsPath))
        using (var first = JsonDocument.Parse((await firstLine.ReadLineAsync(ct))!))
            await _runtime.VerifyLiterarySearchAsync(Id, first.RootElement, ct);
        await File.WriteAllTextAsync(Path.Combine(DirectoryPath, "manifest.json"), JsonSerializer.Serialize(new Manifest(Id, GigaEmbeddingInstallation.Revision, 1024, PointCount, "reference")), ct);
        Ready = true; progress.Report(new("Ready", 100, PointCount.ToString()));
    }
    public static void ValidatePoint(JsonElement point)
    {
        var vector = point.GetProperty("vector");
        if (vector.GetArrayLength() != 1024 || vector.EnumerateArray().Any(x => !float.IsFinite(x.GetSingle())))
            throw new InvalidDataException("Invalid Giga vector.");
        var norm = vector.EnumerateArray().Sum(x => Math.Pow(x.GetDouble(), 2));
        if (Math.Abs(norm - 1) > 0.01 || point.GetProperty("payload").GetProperty("kind").GetString() != "reference")
            throw new InvalidDataException("Invalid reference point.");
    }
    public void SetDestination(string destination)
    {
        if (!Ready) throw new InvalidOperationException("Index is not ready.");
        var temporary = Marker + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Pending(Id, Path.GetFullPath(destination))));
        File.Move(temporary, Marker, true);
    }
    public void CopyInto(string projectStaging)
    {
        if (!Ready) throw new InvalidOperationException("Index is not ready.");
        var folder = Path.Combine(projectStaging, "Rag", "Source"); Directory.CreateDirectory(folder);
        foreach (var name in new[] { "text.json", "sources.json", "vectors.jsonl", "manifest.json" })
            File.Copy(Path.Combine(DirectoryPath, name), Path.Combine(folder, name), false);
    }
    public void Commit() { _committed = true; }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true;
        try
        {
            if (!_committed && _collectionAttempted)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await _runtime.DeleteLiteraryIndexAsync(Id, deadline.Token);
            }
            _lease.Dispose(); DeleteStaging(DirectoryPath, _stagingRoot);
        }
        catch (Exception ex) { OwnedProcessRegistry.Log("rag_cleanup_pending", "Giga", detail: ex.Message); }
        finally { _lease.Dispose(); }
    }
    private static void DeleteStaging(string directory, string stagingRoot)
    {
        var full = Path.GetFullPath(directory);
        if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(stagingRoot), StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(full), "N", out _) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Unsafe staging cleanup path.");
        // Inspect before descending; never walk through a junction to another tree.
        void DeleteTree(string folder)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(folder))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Staging contains links.");
                if ((attributes & FileAttributes.Directory) != 0) DeleteTree(path); else File.Delete(path);
            }
            Directory.Delete(folder, false);
        }
        DeleteTree(full);
    }
    public static async Task RecoverAsync(CancellationToken ct, QdrantRuntime? runtime = null, string? stagingRoot = null)
    {
        var root = Path.GetFullPath(stagingRoot ?? StagingRoot);
        if (!Directory.Exists(root)) return;
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            ct.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) continue;
            try
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked staging folder.");
                using (var lease = new FileStream(Path.Combine(directory, "owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    var marker = Path.Combine(directory, "pending.json");
                    if (!File.Exists(marker)) continue;
                    var pending = JsonSerializer.Deserialize<Pending>(File.ReadAllText(marker)) ?? throw new InvalidDataException("Empty staging marker.");
                    if (pending.Id != Path.GetFileName(directory)) throw new InvalidDataException("Invalid staging marker.");
                    var manifestPath = pending.ProjectPath is null ? "" : Path.Combine(pending.ProjectPath, "Rag", "Source", "manifest.json");
                    var committed = File.Exists(manifestPath) && JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath))?.Id == pending.Id;
                    if (!committed) await (runtime ?? QdrantRuntime.Shared).DeleteLiteraryIndexAsync(pending.Id, ct);
                }
                DeleteStaging(directory, root);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            { OwnedProcessRegistry.Log("rag_recovery_deferred", "Giga", detail: ex.Message); }
        }
    }
    public static void QueueProjectDeletion(string projectPath)
    {
        var path = Path.Combine(projectPath, "Rag", "Source", "manifest.json");
        if (!File.Exists(path)) return;
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path)) ?? throw new InvalidDataException("Invalid RAG manifest.");
        if (!Guid.TryParse(manifest.Id, out var parsedId)) throw new InvalidDataException("Invalid RAG collection identifier.");
        var id = parsedId.ToString("N");
        var folder = Path.Combine(StagingRoot, id); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "pending.json"), JsonSerializer.Serialize(new Pending(id, Path.GetFullPath(projectPath))));
        // Recovery retains the collection if deletion fails and the project still exists.
    }
}
