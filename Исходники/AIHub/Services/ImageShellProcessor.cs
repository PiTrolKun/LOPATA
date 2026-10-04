using System.ComponentModel;
using System.IO;
using AIHub.Models;

namespace AIHub.Services;

public sealed record ImageShellResult(string OutputPath, bool Changed, bool Recovered = false);

public sealed partial class ImageShellProcessor
{
    private readonly ImageUtilityProcessor _processor;
    private readonly Action<string>? _checkpointObserver;

    public ImageShellProcessor(ImageUtilityProcessor? processor = null) => _processor = processor ?? new();
    internal ImageShellProcessor(ImageUtilityProcessor processor, Action<string> checkpointObserver)
    { _processor = processor; _checkpointObserver = checkpointObserver; }

    public async Task<ImageShellResult> ProcessAsync(ImageShellRequest request, string inputPath,
        string transactionDirectory, CancellationToken token = default)
    {
        request = request.Validate();
        var source = Path.GetFullPath(inputPath);
        if (!request.Paths.Contains(source, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("File is outside the shell request.");
        var paths = TransactionPaths.Create(request.RequestId, source, transactionDirectory);
        Lopata.Updates.SafeUpdatePath.RejectLinks(paths.JournalDirectory);
        Lopata.Updates.SafeUpdatePath.RejectLinks(paths.PrivateDirectory);
        Directory.CreateDirectory(paths.JournalDirectory);
        using var transactionLock = new FileStream(paths.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var journal = Load(paths);
        if (journal is not null) ValidateJournal(journal, request, paths);

        if (journal?.State == "complete")
        {
            await VerifyCompletedAsync(journal, token);
            await CleanupBackupAsync(journal, token);
            CleanupPrivateFiles(journal);
            return new(journal.OutputPath!, !journal.NoOp, true);
        }
        if (journal is not null && await IsPublishedAsync(journal, token))
            return await FinishPublishedAsync(journal, paths, token, recovered: true);
        if (journal?.State == "published" || (journal?.Operation == ImageShellOperation.WebP && journal.State == "source-moved"))
            throw new ImageUtilityException("ImageShell.Error.ResultChanged", journal.OutputPath);

        var sourceOrBackup = File.Exists(source) ? source : journal is not null && File.Exists(journal.BackupPath) ? journal.BackupPath : source;
        using var original = new ImageShellFileLease(sourceOrBackup);
        var originalHash = await original.HashAsync(token);
        if (journal is null)
        {
            journal = new Journal
            {
                RequestId = request.RequestId, Operation = request.Operation, SourcePath = source,
                PrivateDirectory = paths.PrivateDirectory, BackupPath = paths.BackupPath, SnapshotPath = paths.SnapshotPath,
                OriginalIdentity = original.Identity, OriginalHash = originalHash
            };
            Save(paths, journal);
        }
        else if (original.Identity != journal.OriginalIdentity || originalHash != journal.OriginalHash)
            throw new ImageUtilityException("ImageShell.Error.SourceChanged", source);
        Directory.CreateDirectory(paths.PrivateDirectory);
        if ((File.GetAttributes(paths.PrivateDirectory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Image transaction directory cannot be a link.");

        if (journal.StagePath is null || !await MatchesAsync(journal.StagePath, journal.NewIdentity, journal.NewHash, token))
        {
            await original.CopyAsync(paths.SnapshotPath, token);
            var info = await _processor.InspectAsync(paths.SnapshotPath, token);
            if (request.Operation == ImageShellOperation.WebP && info.Format.Equals("WEBP", StringComparison.OrdinalIgnoreCase))
            {
                journal.NoOp = true; journal.OutputPath = source; journal.State = "complete";
                Save(paths, journal);
                CleanupPrivateFiles(journal);
                return new(source, false);
            }
            var format = request.Operation == ImageShellOperation.WebP ? ImageUtilityFormats.Get("webp") : ImageUtilityFormats.Get(info.Format);
            var options = new ImageUtilityOptions
            {
                MethodId = "lanczos3", ScaleMultiplier = request.Operation == ImageShellOperation.Upscale2 ? 2 : null,
                FormatOnly = request.Operation == ImageShellOperation.WebP, Format = format.Id, Quality = 100,
                PreserveTransparency = true, CustomName = "result",
                Parameters = new() { ["lossless"] = "true", ["lobes"] = "3" }
            };
            var item = new ImageUtilityItem { Source = paths.SnapshotPath, DisplayName = Path.GetFileName(paths.SnapshotPath) };
            journal.StagePath = await _processor.ProcessAsync(item, options, paths.PrivateDirectory, token: token);
            var outputInfo = await _processor.InspectAsync(journal.StagePath, token);
            var expected = options.FormatOnly ? (info.Width, info.Height) : (checked(info.Width * 2), checked(info.Height * 2));
            if ((outputInfo.Width, outputInfo.Height) != expected || outputInfo.Frames != info.Frames)
                throw new ImageUtilityException("ImageUtility.Error.ResultDimensions");
            journal.NewHash = await HashAsync(journal.StagePath, token);
            journal.NewIdentity = ImageShellFileLease.ReadIdentity(journal.StagePath);
            journal.OutputPath ??= request.Operation == ImageShellOperation.Upscale2 ? source : NextWebPPath(source);
            journal.State = "prepared";
            Save(paths, journal);
        }
        token.ThrowIfCancellationRequested();
        // The lease still refers to the original identity and excludes external writes/deletes.
        if (await original.HashAsync(token) != journal.OriginalHash)
            throw new ImageUtilityException("ImageShell.Error.SourceChanged", source);
        using var replacement = new ImageShellFileLease(journal.StagePath!);
        if (replacement.Identity != journal.NewIdentity || await replacement.HashAsync(token) != journal.NewHash)
            throw new ImageUtilityException("ImageShell.Error.ResultChanged", journal.StagePath);
        token.ThrowIfCancellationRequested();

        if (request.Operation == ImageShellOperation.WebP)
        {
            while (true)
            {
                try { replacement.Rename(journal.OutputPath!); break; }
                catch (Exception ex) when ((ex is IOException or Win32Exception) && (File.Exists(journal.OutputPath) || Directory.Exists(journal.OutputPath)))
                {
                    journal.OutputPath = NextWebPPath(source);
                    Save(paths, journal);
                }
            }
            if (!await IsPublishedAsync(journal, CancellationToken.None)) throw new ImageUtilityException("ImageShell.Error.ResultChanged", journal.OutputPath);
            journal.State = "published"; Save(paths, journal);
            if (!sourceOrBackup.Equals(paths.BackupPath, StringComparison.OrdinalIgnoreCase)) original.Rename(paths.BackupPath);
            journal.State = "source-moved"; Save(paths, journal);
        }
        else
        {
            if (!sourceOrBackup.Equals(paths.BackupPath, StringComparison.OrdinalIgnoreCase)) original.Rename(paths.BackupPath);
            journal.State = "source-moved"; Save(paths, journal);
            try
            {
                replacement.Rename(source);
                if (!await IsPublishedAsync(journal, CancellationToken.None)) throw new ImageUtilityException("ImageShell.Error.ResultChanged", source);
                journal.State = "published"; Save(paths, journal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
            {
                // Never overwrite a competing file. The original remains recoverable in the journal's backup.
                if (!File.Exists(source))
                {
                    original.Rename(source);
                    journal.State = "prepared"; Save(paths, journal);
                }
                throw;
            }
        }
        journal.State = "complete"; Save(paths, journal);
        original.DeleteOnClose();
        original.Dispose();
        CleanupPrivateFiles(journal);
        return new(journal.OutputPath!, true);
    }

    private async Task<ImageShellResult> FinishPublishedAsync(Journal journal, TransactionPaths paths, CancellationToken token, bool recovered)
    {
        using var replacement = new ImageShellFileLease(journal.OutputPath!);
        if (replacement.Identity != journal.NewIdentity || await replacement.HashAsync(token) != journal.NewHash)
            throw new ImageUtilityException("ImageShell.Error.ResultChanged", journal.OutputPath);
        if (journal.Operation == ImageShellOperation.WebP && File.Exists(journal.SourcePath))
        {
            using var source = new ImageShellFileLease(journal.SourcePath);
            if (source.Identity != journal.OriginalIdentity || await source.HashAsync(token) != journal.OriginalHash)
                throw new ImageUtilityException("ImageShell.Error.SourceChanged", journal.SourcePath);
            // Result identity and bytes were validated before the source is removed.
            if (!await IsPublishedAsync(journal, token)) throw new ImageUtilityException("ImageShell.Error.ResultChanged", journal.OutputPath);
            source.Rename(journal.BackupPath);
            journal.State = "complete"; Save(paths, journal);
            source.DeleteOnClose();
        }
        else
        {
            journal.State = "complete"; Save(paths, journal);
            replacement.Dispose();
            await CleanupBackupAsync(journal, token);
        }
        CleanupPrivateFiles(journal);
        return new(journal.OutputPath!, true, recovered);
    }
}
