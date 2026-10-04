using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

public sealed partial class ImageShellProcessor
{
    private sealed class Journal
    {
        public string RequestId { get; set; } = "";
        public ImageShellOperation Operation { get; set; }
        public string State { get; set; } = "created";
        public string SourcePath { get; set; } = "";
        public string PrivateDirectory { get; set; } = "";
        public string SnapshotPath { get; set; } = "";
        public string BackupPath { get; set; } = "";
        public string OriginalIdentity { get; set; } = "";
        public string OriginalHash { get; set; } = "";
        public string? StagePath { get; set; }
        public string? OutputPath { get; set; }
        public string? NewIdentity { get; set; }
        public string? NewHash { get; set; }
        public bool NoOp { get; set; }
    }

    private sealed record TransactionPaths(string JournalDirectory, string JournalPath, string LockPath,
        string PrivateDirectory, string BackupPath, string SnapshotPath, string SourcePath)
    {
        public static TransactionPaths Create(string requestId, string source, string journalDirectory)
        {
            journalDirectory = Path.GetFullPath(journalDirectory);
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.ToUpperInvariant())))[..16];
            var stem = requestId + "-" + key;
            var privateDirectory = Path.Combine(Path.GetDirectoryName(source)!, ".lopata-" + stem);
            // Preserve a known extension for formats whose content has no identifying header.
            var extension = Path.GetExtension(source).ToLowerInvariant();
            if (!ImageUtilitySources.SupportedExtensions.Contains(extension)) extension = ".img";
            return new(journalDirectory, Path.Combine(journalDirectory, stem + ".json"),
                Path.Combine(journalDirectory, stem + ".lock"), privateDirectory,
                Path.Combine(privateDirectory, "original.backup"), Path.Combine(privateDirectory, "source" + extension), source);
        }
    }

    private static Journal? Load(TransactionPaths paths) => !File.Exists(paths.JournalPath) ? null :
        JsonSerializer.Deserialize<Journal>(File.ReadAllText(paths.JournalPath)) ?? throw new InvalidDataException("Invalid image transaction receipt.");

    private static void ValidateJournal(Journal journal, ImageShellRequest request, TransactionPaths paths)
    {
        static bool Same(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        if (journal.RequestId != request.RequestId || journal.Operation != request.Operation ||
            !Same(journal.SourcePath, paths.SourcePath) || !Same(journal.PrivateDirectory, paths.PrivateDirectory) ||
            !Same(journal.BackupPath, paths.BackupPath) || !Same(journal.SnapshotPath, paths.SnapshotPath) ||
            journal.State is not ("created" or "prepared" or "source-moved" or "published" or "complete") ||
            journal.OriginalHash.Length != 64 || string.IsNullOrEmpty(journal.OriginalIdentity))
            throw new InvalidDataException("Image transaction does not match this request.");
        if (journal.StagePath is not null && !Same(Path.GetDirectoryName(Path.GetFullPath(journal.StagePath)), paths.PrivateDirectory))
            throw new InvalidDataException("Image transaction stage is outside its private directory.");
        if (journal.OutputPath is not null)
        {
            var output = Path.GetFullPath(journal.OutputPath);
            if (request.Operation == ImageShellOperation.Upscale2 || journal.NoOp)
            {
                if (!Same(output, paths.SourcePath)) throw new InvalidDataException("Invalid replacement destination.");
            }
            else if (!Same(Path.GetDirectoryName(output), Path.GetDirectoryName(paths.SourcePath)) ||
                !Path.GetExtension(output).Equals(".webp", StringComparison.OrdinalIgnoreCase) || Same(output, paths.SourcePath))
                throw new InvalidDataException("Invalid WebP destination.");
        }
    }

    private void Save(TransactionPaths paths, Journal journal)
    {
        var temporary = paths.JournalPath + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, journal);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, paths.JournalPath, overwrite: true);
        _checkpointObserver?.Invoke(journal.State);
    }

    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
    }

    private static async Task<bool> MatchesAsync(string path, string? identity, string? hash, CancellationToken token)
    {
        if (identity is null || hash is null || !File.Exists(path)) return false;
        // A matching hash alone is insufficient: a pre-existing identical image is not our publication.
        return ImageShellFileLease.ReadIdentity(path) == identity && await HashAsync(path, token) == hash &&
            ImageShellFileLease.ReadIdentity(path) == identity;
    }

    private static Task<bool> IsPublishedAsync(Journal journal, CancellationToken token) =>
        journal.OutputPath is null || journal.NoOp ? Task.FromResult(false) :
        MatchesAsync(journal.OutputPath, journal.NewIdentity, journal.NewHash, token);

    private static async Task VerifyCompletedAsync(Journal journal, CancellationToken token)
    {
        var matches = journal.NoOp
            ? await MatchesAsync(journal.SourcePath, journal.OriginalIdentity, journal.OriginalHash, token)
            : await IsPublishedAsync(journal, token);
        if (!matches) throw new ImageUtilityException("ImageShell.Error.ResultChanged", journal.OutputPath);
    }

    private static async Task CleanupBackupAsync(Journal journal, CancellationToken token)
    {
        if (!File.Exists(journal.BackupPath)) return;
        using var output = new ImageShellFileLease(journal.OutputPath!);
        if (output.Identity != journal.NewIdentity || await output.HashAsync(token) != journal.NewHash)
            throw new ImageUtilityException("ImageShell.Error.ResultChanged", journal.OutputPath);
        using var original = new ImageShellFileLease(journal.BackupPath);
        if (original.Identity != journal.OriginalIdentity || await original.HashAsync(token) != journal.OriginalHash)
            throw new ImageUtilityException("ImageShell.Error.SourceChanged", journal.BackupPath);
        original.DeleteOnClose();
    }

    private static void CleanupPrivateFiles(Journal journal)
    {
        // Only our snapshot is disposable; an original backup is removed through a verified handle above.
        try { if (File.Exists(journal.SnapshotPath)) File.Delete(journal.SnapshotPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        try { if (Directory.Exists(journal.PrivateDirectory)) Directory.Delete(journal.PrivateDirectory, recursive: false); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string NextWebPPath(string source)
    {
        var folder = Path.GetDirectoryName(source)!;
        var stem = Path.GetFileNameWithoutExtension(source);
        for (var number = 0; ; number++)
        {
            var path = Path.Combine(folder, stem + (number == 0 ? "" : $"_{number}") + ".webp");
            if (!File.Exists(path) && !Directory.Exists(path)) return path;
        }
    }
}
