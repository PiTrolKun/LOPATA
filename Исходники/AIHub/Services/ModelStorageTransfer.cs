using System.IO;
using System.Security.Cryptography;
using AIHub.Models;
using Lopata.Updates;

namespace AIHub.Services;

internal sealed record ModelTransferFile(string RelativePath, long Bytes, DateTime ModifiedUtc, string Sha256);
internal sealed record ModelTransferItem(ModelStorageAddress Address, string SourceRoot, string Source,
    string DisplayName, ModelTransferFile[] Files);
internal sealed record ModelTransferPlan(string Id, string TargetRoot, ModelTransferItem[] Items)
{
    public long Bytes => Items.Sum(item => item.Files.Sum(file => file.Bytes));
}
internal sealed record ModelTransferResult(long ReleasedBytes, bool CleanupPending);

internal sealed class ModelStorageTransfer(ModelStorageAddresses addresses,
    Func<IReadOnlyList<ModelStorageCopyItem>, nint, CancellationToken, Task>? copy = null)
{
    private readonly Func<IReadOnlyList<ModelStorageCopyItem>, nint, CancellationToken, Task> _copy =
        copy ?? ModelStorageWindowsCopy.CopyAsync;

    internal ModelTransferPlan Plan(IEnumerable<ManagedModelArtifactCard> cards, string destination)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        SafeUpdatePath.RejectLinks(root);
        if (!Directory.Exists(root)) throw new IOException("ModelStorage.FolderUnavailable");
        var locations = addresses.Read();
        var items = new List<ModelTransferItem>();
        var sources = new HashSet<string>(ModelStorageAddresses.Paths);
        var destinations = new HashSet<string>(ModelStorageAddresses.Paths);
        foreach (var card in cards.DistinctBy(card => (card.ModelArtifactId, card.Revision)))
        {
            ModelStorageAddresses.Apply(card, locations);
            if (!card.IsManaged || card.Files.Count == 0 || string.IsNullOrWhiteSpace(card.InstallDirectory)
                || string.IsNullOrWhiteSpace(card.ModelsRoot)) throw new InvalidDataException("ModelStorage.ModelUnavailable");
            var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(card.InstallDirectory));
            var sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(card.ModelsRoot));
            var target = Path.GetFullPath(Path.Combine(root, Path.GetRelativePath(sourceRoot, source)));
            CheckBelow(sourceRoot, source);
            CheckBelow(root, target);
            if (ModelStorageAddresses.Paths.Equals(source, target)) continue;
            if (ModelStorageAddresses.IsBelow(source, root) || ModelStorageAddresses.IsBelow(source, target)
                || Directory.Exists(target) || File.Exists(target)
                || !sources.Add(source) || !destinations.Add(target)) throw new IOException("ModelStorage.Conflict");

            var files = new List<ModelTransferFile>();
            foreach (var file in card.Files)
            {
                var path = FilePath(source, file.RelativePath);
                var info = new FileInfo(path);
                if (!file.IsRequired && !info.Exists) continue;
                if (!info.Exists || (file.SizeBytes > 0 && info.Length != file.SizeBytes)
                    || TransferInProgress(path)) throw new IOException("ModelStorage.ModelUnavailable");
                files.Add(new(file.RelativePath, info.Length, info.LastWriteTimeUtc, file.Sha256));
            }
            foreach (var sidecar in ModelStorageCatalog.Sidecars(card))
            {
                var info = new FileInfo(FilePath(source, sidecar));
                if (!info.Exists) throw new IOException("ModelStorage.ModelUnavailable");
                files.Add(new(sidecar, info.Length, info.LastWriteTimeUtc, ""));
            }
            if (files.Count == 0 || files.Select(file => FilePath(source, file.RelativePath))
                    .Distinct(ModelStorageAddresses.Paths).Count() != files.Count)
                throw new InvalidDataException("ModelStorage.Conflict");
            var previous = locations.FirstOrDefault(item => ModelStorageAddresses.Matches(item, card.ModelArtifactId, card.Revision));
            var aliases = (previous?.PreviousDirectories ?? []).Append(source)
                .Where(path => !ModelStorageAddresses.Paths.Equals(path, target)).Distinct(ModelStorageAddresses.Paths).ToArray();
            items.Add(new(new(card.ModelArtifactId, card.Revision, root, target, aliases), sourceRoot,
                source, card.DisplayName, [.. files]));
        }
        if (items.Any(item => items.Any(other => ModelStorageAddresses.IsBelow(item.Source, other.Source)
            || ModelStorageAddresses.IsBelow(item.Address.Directory, other.Address.Directory)
            || ModelStorageAddresses.IsBelow(item.Source, other.Address.Directory)
            || ModelStorageAddresses.IsBelow(other.Address.Directory, item.Source))))
            throw new IOException("ModelStorage.Conflict");
        var plan = new ModelTransferPlan(Guid.NewGuid().ToString("N"), root, [.. items]);
        EnsureSpace(plan);
        return plan;
    }

    internal async Task<ModelTransferResult> MoveAsync(ModelTransferPlan plan, nint owner, Action verifying, CancellationToken token)
    {
        if (plan.Items.Length == 0) return new(0, false);
        if (File.Exists(addresses.JournalPath)) throw new IOException("ModelStorage.RecoveryPending");
        EnsureSpace(plan);
        ValidatePaths(plan);
        if (plan.Items.Any(item => Directory.Exists(item.Address.Directory) || File.Exists(item.Address.Directory)))
            throw new IOException("ModelStorage.Conflict");
        EnsureSourceUnchanged(plan, checkInUse: true);
        ModelStorageAddresses.WriteDocument(addresses.JournalPath, plan);
        try
        {
            var staged = Stage(plan);
            Directory.CreateDirectory(staged);
            var copies = plan.Items.SelectMany((item, index) => item.Files.Select(file => new ModelStorageCopyItem(
                FilePath(item.Source, file.RelativePath), Path.GetDirectoryName(FilePath(StageItem(plan, index), file.RelativePath))!))).ToArray();
            await _copy(copies, owner, token);
            verifying();
            plan = await VerifyAsync(plan, token);
            ModelStorageAddresses.WriteDocument(addresses.JournalPath, plan);
            EnsureSourceUnchanged(plan);
            for (var index = 0; index < plan.Items.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                var target = plan.Items[index].Address.Directory;
                CheckBelow(plan.TargetRoot, target);
                File.WriteAllText(Path.Combine(StageItem(plan, index), Marker(plan)), plan.Id);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                Directory.Move(StageItem(plan, index), target);
            }
            token.ThrowIfCancellationRequested();
            var current = addresses.Read().Where(address => !plan.Items.Any(item =>
                ModelStorageAddresses.Matches(address, item.Address.ArtifactId, item.Address.Revision)));
            addresses.Publish([.. current, .. plan.Items.Select(item => item.Address)]);
        }
        catch
        {
            TryRecover();
            throw;
        }
        return TryRecover(verifiedInCurrentMove: true);
    }

    internal ModelTransferResult TryRecover(bool verifiedInCurrentMove = false, bool leaveCommittedForLater = false)
    {
        if (!File.Exists(addresses.JournalPath)) return new(0, false);
        long released = 0;
        try
        {
            var plan = ModelStorageAddresses.ReadDocument<ModelTransferPlan>(addresses.JournalPath);
            ValidatePaths(plan);
            var current = addresses.Read();
            var committed = plan.Items.All(item => current.Any(address =>
                ModelStorageAddresses.Matches(address, item.Address.ArtifactId, item.Address.Revision)
                && ModelStorageAddresses.Paths.Equals(address.Directory, item.Address.Directory)));
            if (!Directory.Exists(plan.TargetRoot)) return new(0, true);
            if (committed)
            {
                if (leaveCommittedForLater) return new(0, true);
                foreach (var item in plan.Items)
                    foreach (var file in item.Files)
                    {
                        var target = new FileInfo(FilePath(item.Address.Directory, file.RelativePath));
                        if (!target.Exists || target.Length != file.Bytes) return new(0, true);
                        if (!verifiedInCurrentMove && !string.Equals(HashAsync(target.FullName, CancellationToken.None).GetAwaiter().GetResult(),
                                file.Sha256, StringComparison.OrdinalIgnoreCase)) return new(0, true);
                    }
                foreach (var item in plan.Items)
                {
                    foreach (var file in item.Files)
                    {
                        var source = new FileInfo(FilePath(item.Source, file.RelativePath));
                        if (!source.Exists)
                        {
                            if (!Directory.Exists(item.SourceRoot)) return new(released, true);
                            continue;
                        }
                        if (source.Length != file.Bytes || source.LastWriteTimeUtc != file.ModifiedUtc) return new(released, true);
                        source.Delete();
                        released += file.Bytes;
                    }
                    RemoveEmptyDirectories(item.Source, item.Files);
                    File.Delete(FilePath(item.Address.Directory, Marker(plan)));
                }
            }
            else
            {
                foreach (var item in plan.Items)
                {
                    var marker = FilePath(item.Address.Directory, Marker(plan));
                    if (File.Exists(marker) && File.ReadAllText(marker) == plan.Id)
                        CleanStagedFiles(item.Address.Directory, item.Files, marker);
                }
            }
            for (var index = 0; index < plan.Items.Length; index++)
            {
                var stage = StageItem(plan, index);
                CleanStagedFiles(stage, plan.Items[index].Files, FilePath(stage, Marker(plan)));
            }
            if (Directory.Exists(Stage(plan))) Directory.Delete(Stage(plan));
            File.Delete(addresses.JournalPath);
            return new(released, false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
            or ArgumentException or System.Text.Json.JsonException)
        {
            OwnedProcessRegistry.Log("model_transfer_recovery_pending", "ModelStorage", detail: error.GetType().Name);
            return new(released, true);
        }
    }

    private static async Task<ModelTransferPlan> VerifyAsync(ModelTransferPlan plan, CancellationToken token)
    {
        var verifiedItems = new List<ModelTransferItem>();
        for (var index = 0; index < plan.Items.Length; index++)
        {
            var item = plan.Items[index];
            var verifiedFiles = new List<ModelTransferFile>();
            foreach (var file in item.Files)
            {
                token.ThrowIfCancellationRequested();
                var target = new FileInfo(FilePath(StageItem(plan, index), file.RelativePath));
                var expected = file.Sha256;
                if (!target.Exists || target.Length != file.Bytes) throw new InvalidDataException("ModelStorage.VerificationFailed");
                if (string.IsNullOrEmpty(expected)) expected = await HashAsync(FilePath(item.Source, file.RelativePath), token);
                if (!string.Equals(await HashAsync(target.FullName, token), expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("ModelStorage.VerificationFailed");
                verifiedFiles.Add(file with { Sha256 = expected });
            }
            verifiedItems.Add(item with { Files = [.. verifiedFiles] });
        }
        return plan with { Items = [.. verifiedItems] };
    }

    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
    }

    private static void EnsureSpace(ModelTransferPlan plan)
    {
        var free = new DriveInfo(Path.GetPathRoot(plan.TargetRoot)!).AvailableFreeSpace;
        if (plan.Bytes > free) throw new ModelStorageSpaceException(plan.Bytes, free);
    }

    private static void EnsureSourceUnchanged(ModelTransferPlan plan, bool checkInUse = false)
    {
        foreach (var item in plan.Items)
        foreach (var file in item.Files)
        {
            var info = new FileInfo(FilePath(item.Source, file.RelativePath));
            if (!info.Exists || info.Length != file.Bytes || info.LastWriteTimeUtc != file.ModifiedUtc || TransferInProgress(info.FullName))
                throw new IOException("ModelStorage.ModelUnavailable");
            if (!checkInUse) continue;
            try
            {
                using var source = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.None);
            }
            catch (IOException error) when (error.HResult == unchecked((int)0x80070020) || error.HResult == unchecked((int)0x80070021))
            {
                throw new IOException("ModelStorage.Busy", error);
            }
        }
    }

    private static bool TransferInProgress(string path) => File.Exists(path + ".part");
    private static string Stage(ModelTransferPlan plan) => Path.Combine(plan.TargetRoot, ".lopata-transfer-" + plan.Id);
    private static string StageItem(ModelTransferPlan plan, int index) => Path.Combine(Stage(plan), index.ToString(System.Globalization.CultureInfo.InvariantCulture));
    private static string Marker(ModelTransferPlan plan) => ".lopata-model-" + plan.Id;

    private static void ValidatePaths(ModelTransferPlan plan)
    {
        if (!Guid.TryParseExact(plan.Id, "N", out _) || plan.Items.Length == 0) throw new InvalidDataException("ModelStorage.Conflict");
        CheckBelow(plan.TargetRoot, Stage(plan));
        foreach (var item in plan.Items)
        {
            CheckBelow(item.SourceRoot, item.Source);
            CheckBelow(plan.TargetRoot, item.Address.Directory);
            if (!ModelStorageAddresses.Paths.Equals(item.Address.ModelsRoot, plan.TargetRoot)
                || ModelStorageAddresses.Paths.Equals(item.Source, item.Address.Directory)
                || ModelStorageAddresses.IsBelow(item.Source, item.Address.Directory))
                throw new InvalidDataException("ModelStorage.Conflict");
        }
    }

    private static string FilePath(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains(':')) throw new InvalidDataException("ModelStorage.Conflict");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        CheckBelow(root, path);
        return path;
    }

    private static void CheckBelow(string root, string path)
    {
        if (!ModelStorageAddresses.IsBelow(root, path)) throw new InvalidDataException("ModelStorage.Conflict");
        SafeUpdatePath.RejectLinks(path);
    }

    private static void CleanStagedFiles(string root, ModelTransferFile[] files, string marker)
    {
        if (!Directory.Exists(root)) return;
        foreach (var file in files)
        {
            var path = FilePath(root, file.RelativePath);
            if (File.Exists(path)) File.Delete(path);
        }
        if (File.Exists(marker)) File.Delete(marker);
        RemoveEmptyDirectories(root, files);
    }

    private static void RemoveEmptyDirectories(string root, ModelTransferFile[] files)
    {
        var directories = new HashSet<string>(ModelStorageAddresses.Paths) { root };
        foreach (var file in files)
        {
            var parent = Path.GetDirectoryName(FilePath(root, file.RelativePath))!;
            for (; !ModelStorageAddresses.Paths.Equals(parent, root); parent = Path.GetDirectoryName(parent)!)
                directories.Add(parent);
        }
        foreach (var directory in directories.OrderByDescending(path => path.Length))
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
    }
}

internal sealed class ModelStorageSpaceException(long required, long available) : IOException("ModelStorage.Space")
{
    public long Required { get; } = required;
    public long Available { get; } = available;
}
