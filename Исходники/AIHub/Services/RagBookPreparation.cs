using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

public sealed record RagBookJob(string Id, string Name, string Directory, string ModelsRoot, string[] Books)
{
    public int CpuTargetPercent { get; init; } = 50;
}
public sealed record RagBookProgress(string Stage, int Done = 0, int Total = 0, string Detail = "");

/// <summary>Page-independent orchestration. Every attempt uses durable, content-addressed source snapshots.</summary>
public sealed class RagBookPreparation
{
    public const string Kind = "rag.books.prepare";
    public static string LibraryDirectory(string name, string output)
    {
        if (string.IsNullOrWhiteSpace(name) || !Path.IsPathFullyQualified(output)) throw new InvalidDataException("RagBooks.InvalidInput");
        var key = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name.Trim())))[..16];
        return Path.Combine(output, "library-" + key);
    }
    public static RagBookJob Create(string name, string output, string models, IEnumerable<string> books)
    {
        if (string.IsNullOrWhiteSpace(name) || !Path.IsPathFullyQualified(output)) throw new InvalidDataException("RagBooks.InvalidInput");
        var paths = books.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length == 0 || paths.Any(p => !File.Exists(p))) throw new InvalidDataException("RagBooks.SourceMissing");
        return new(Guid.NewGuid().ToString("N"), name.Trim(), LibraryDirectory(name, output), Path.GetFullPath(models), paths);
    }

    public static RagBookJob Read(BackgroundOperationState state)
    {
        var job = state.Input.Deserialize<RagBookJob>() ?? throw new InvalidDataException("Missing book job.");
        if (state.Kind != Kind || !Guid.TryParseExact(job.Id, "N", out _) || !Path.IsPathFullyQualified(job.Directory)
            || !Path.IsPathFullyQualified(job.ModelsRoot) || job.Books is not { Length: > 0 }) throw new InvalidDataException("Invalid book job.");
        return job;
    }

    public async Task<string> RunAsync(RagBookJob job, string title, IProgress<RagBookProgress> progress,
        CancellationToken token, BackgroundOperationState? restored = null)
    {
        var packages = job.Books.Where(RagLexiconPackage.IsPackage).ToArray();
        var books = job.Books.Where(p => !RagLexiconPackage.IsPackage(p)).ToArray();
        if (books.Length > 0 && restored is null && !RagBookModelPreparation.FilesPresent(RagBookModels.Cards(job.ModelsRoot).Single(card => card.ModelArtifactId == RagBookModels.EmbeddingId)))
            throw new InvalidDataException("RagBooks.FilesMissing");
        return await ApplicationBackgroundOperations.RunAsync(Kind, title, job.Directory, job, async ct =>
        {
            var work = Path.Combine(job.Directory, "work", job.Id);
            Directory.CreateDirectory(work);
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(work))!);
            var minimum = job.Books.Sum(p => new FileInfo(p).Length) * 2 + 64L * 1024 * 1024;
            if (drive.IsReady && drive.AvailableFreeSpace < minimum) throw new IOException("RagBooks.InsufficientSpace");
            AtomicJson(Path.Combine(work, "job.json"), job);
            if (packages.Length > 0)
            {
                var input = await Task.Run(() => RagLexiconPackage.Prepare(packages, job.Directory, work, progress, ct), ct);
                ApplicationBackgroundOperations.Current?.SaveCheckpoint(new { job.Id, Work = work });
                await RagLexiconWorker.RunAsync(input, work, job.Directory, job.CpuTargetPercent, progress, ct);
            }
            if (books.Length == 0) return job.Directory;
            progress.Report(new("Verifying"));
            using var models = new RagBookModelPreparation(job.ModelsRoot, 0);
            var card = await models.PrepareAsync(RagBookModels.EmbeddingId, false,
                new InlineProgress<ManagedModelDownloadProgress>(_ => { }), ct);
            if (card.Status != ManagedModelStatuses.Installed) throw new BackgroundOperationWaitingException("RagBooks.FilesMissing");
            await ComponentLicenseGate.EnsureAsync(RagBookModels.EmbeddingId, ct);
            var sources = await Task.Run(() => RagBookSourcePreparation.Prepare(job with { Books = books }, work, progress, ct), ct);
            AtomicJson(Path.Combine(work, "input.json"), new { schema = 1, job.Id, job.Name, revision = card.Revision, sources });
            ApplicationBackgroundOperations.Current?.SaveCheckpoint(new { job.Id, Work = work });
            await RagBookWorker.RunAsync(card.InstallDirectory, Path.Combine(work, "input.json"), work, job.Directory, false, progress, ct, job.CpuTargetPercent);
            return job.Directory;
        }, token, restored);
    }

    public static void AtomicJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(output, value); output.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
