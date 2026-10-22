using System.IO;
using System.IO.Compression;
using AIHub.Models;

namespace AIHub.Services;

public interface IImageGenerationPreparation : IDisposable
{
    int MaximumParallelConnections { get; set; }
    bool IsReady(string root, string modelId);
    IReadOnlyList<ManagedModelArtifactCard> Register(string root, string modelId);
    Task<IReadOnlyList<ManagedModelArtifactCard>> CheckAsync(string root, string modelId, IProgress<ManagedModelDownloadProgress>? progress, CancellationToken token);
    Task<IReadOnlyList<ManagedModelArtifactCard>> PrepareAsync(string root, string modelId, bool download, IProgress<ManagedModelDownloadProgress>? progress, CancellationToken token);
}

public sealed class ImageGenerationInstallation : IImageGenerationPreparation
{
    private readonly ManagedModelLibraryStore _store;
    private readonly ManagedModelAcquisitionService _downloads;
    public ImageGenerationInstallation(ManagedModelLibraryStore? store = null)
    { _store = store ?? new(); _downloads = new(_store); }
    public int MaximumParallelConnections { get => _downloads.MaximumParallelConnections; set => _downloads.MaximumParallelConnections = value; }
    public IReadOnlyList<ManagedModelArtifactCard> Register(string root, string modelId)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new InvalidOperationException("Generation.StorageRequired");
        root = Path.GetFullPath(root);
        var components = ImageGenerationCatalog.Get(modelId).Components
            .Where(id => id != "generation-runtime" || SdRuntimeSelector.AvailableExecutable() is null).ToArray();
        return ImageGenerationCatalog.CreateCards(root).Where(c => components.Contains(c.ModelArtifactId)).Select(card =>
        {
            var prior = _store.Load(card.ModelArtifactId);
            if (prior is not null && ManagedModelPathIdentity.SameDirectory(prior.InstallDirectory, card.InstallDirectory))
            { card.Status = prior.Status; card.StoredBytes = prior.StoredBytes; }
            return _store.Upsert(card);
        }).ToArray();
    }
    public bool IsReady(string root, string modelId)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        return Register(root, modelId).All(card => card.Status == ManagedModelStatuses.Installed && card.Files.All(file =>
        {
            var info = new FileInfo(Path.Combine(card.InstallDirectory, file.RelativePath));
            return info.Exists && (!ComponentAudit.FullRequired
                || info.Length == file.VerifiedSizeBytes && info.LastWriteTimeUtc == file.VerifiedLastWriteTimeUtc);
        }));
    }
    public async Task<IReadOnlyList<ManagedModelArtifactCard>> PrepareAsync(string root, string modelId, bool download,
        IProgress<ManagedModelDownloadProgress>? progress, CancellationToken token)
    {
        using var installation = download ? ComponentAudit.Installation() : null;
        using var auditOperation = ComponentAudit.CaptureOperation();
        var cards = Register(root, modelId);
        // Confirm every inherited component before any download or model use.
        await ComponentLicenseGate.EnsureAsync(cards.Select(c => c.ModelArtifactId).ToArray(), token);
        foreach (var card in cards)
        {
            token.ThrowIfCancellationRequested();
            var verified = await _downloads.VerifyAsync(card.ModelArtifactId, progress, token);
            if (verified.Status != ManagedModelStatuses.Installed)
            {
                if (!download) throw new InvalidOperationException("Generation.DownloadRequired");
                verified = await _downloads.DownloadAsync(card.ModelArtifactId, progress, token);
                if (verified.Status != ManagedModelStatuses.Installed) throw new InvalidDataException("Generation.VerificationFailed");
            }
        }
        cards = Register(root, modelId);
        if (cards.SingleOrDefault(c => c.ModelArtifactId == "generation-runtime") is { } legacyRuntime)
            await EnsureRuntimeAsync(legacyRuntime, token);
        return cards;
    }
    public async Task<IReadOnlyList<ManagedModelArtifactCard>> CheckAsync(string root, string modelId,
        IProgress<ManagedModelDownloadProgress>? progress, CancellationToken token)
    {
        using var auditOperation = ComponentAudit.CaptureOperation();
        var states = new List<ManagedModelArtifactCard>();
        foreach (var card in Register(root, modelId))
        {
            token.ThrowIfCancellationRequested();
            states.Add(await _downloads.VerifyAsync(card.ModelArtifactId, progress, token));
        }
        return states;
    }
    public static string Executable(IReadOnlyList<ManagedModelArtifactCard> cards)
    {
        if (SdRuntimeSelector.AvailableExecutable() is { } managed) return managed;
        var runtime = cards.Single(c => c.ModelArtifactId == "generation-runtime");
        var executable = RuntimePaths(runtime).Single(path => Path.GetFileName(path).Equals("sd-cli.exe", StringComparison.OrdinalIgnoreCase));
        if (!File.Exists(executable)) throw new FileNotFoundException("Generation.DownloadRequired");
        return executable;
    }
    public static string Weight(IReadOnlyList<ManagedModelArtifactCard> cards, string purpose)
    {
        var pairs = cards.SelectMany(c => c.Files.Where(f => f.Purpose == purpose).Select(f => Path.Combine(c.InstallDirectory, f.RelativePath))).ToArray();
        return pairs.Single();
    }
    internal static Task EnsureRuntimeAsync(ManagedModelArtifactCard card, CancellationToken token) =>
        ComponentAudit.RunAsync("image-generation-legacy-runtime", Path.Combine(card.InstallDirectory, "bin"),
            card.Revision + "/" + string.Join('\n', card.Files.OrderBy(file => file.RelativePath, StringComparer.Ordinal)
                .Select(file => $"{file.RelativePath}|{file.SizeBytes}|{file.Sha256}")), cancellation => {
                    VerifyRuntimePresence(card, cancellation); return Task.CompletedTask;
                }, async cancellation => {
                    await Task.Run(() => EnsureRuntime(card, cancellation), cancellation);
                    VerifyRuntimePresence(card, cancellation);
                }, token);

    private static IReadOnlyList<string> RuntimePaths(ManagedModelArtifactCard card)
    {
        var destination = Path.Combine(card.InstallDirectory, "bin");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in card.Files.Where(file => file.IsRequired))
        {
            var source = PinnedZipExpansionVerifier.ResolveArchivePath(card.InstallDirectory, file.RelativePath.Replace('\\', '/'));
            using var archive = ZipFile.OpenRead(source);
            if (archive.Entries.Count is < 1 or > 4096) throw new InvalidDataException("Invalid runtime archive inventory.");
            foreach (var entry in archive.Entries.Where(entry => !entry.FullName.EndsWith('/')))
                paths.Add(PinnedZipExpansionVerifier.ResolveArchivePath(destination, entry.FullName));
        }
        return paths.ToArray();
    }
    private static void VerifyRuntimePresence(ManagedModelArtifactCard card, CancellationToken token)
    {
        foreach (var path in RuntimePaths(card).Append(Path.Combine(card.InstallDirectory, "bin", "ready.txt")))
        {
            token.ThrowIfCancellationRequested();
            if (!File.Exists(path)) throw new FileNotFoundException("Generation.DownloadRequired", path);
        }
    }
    private static void EnsureRuntime(ManagedModelArtifactCard card, CancellationToken token)
    {
        var destination = Path.Combine(card.InstallDirectory, "bin");
        try { VerifyRuntimePresence(card, token); return; }
        catch (FileNotFoundException) { }
        using var installation = ComponentAudit.Installation();
        var staging = destination + "." + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var archive in card.Files)
                ExtractArchive(Path.Combine(card.InstallDirectory, archive.RelativePath), staging, token);
            var executable = Directory.EnumerateFiles(staging, "sd-cli.exe", SearchOption.AllDirectories).Single();
            var executableDirectory = Path.GetDirectoryName(executable)!;
            foreach (var library in Directory.EnumerateFiles(staging, "*.dll", SearchOption.AllDirectories).ToArray())
                if (!string.Equals(Path.GetDirectoryName(library), executableDirectory, StringComparison.OrdinalIgnoreCase))
                    File.Copy(library, Path.Combine(executableDirectory, Path.GetFileName(library)), false);
            File.WriteAllText(Path.Combine(staging, "ready.txt"), ImageGenerationCatalog.Manifest.BackendCommit);
            if (Directory.Exists(destination)) Directory.Move(destination, destination + ".old." + Guid.NewGuid().ToString("N"));
            Directory.Move(staging, destination);
            ComponentAudit.Invalidate(destination);
        }
        finally
        {
            // Temporary files belong exclusively to this extraction attempt.
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }
    public static void ExtractArchive(string source, string destination, CancellationToken token)
    {
        var root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(source);
        long bytes = 0;
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            var target = Path.GetFullPath(Path.Combine(root, relative));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase) || relative.Contains(':')
                || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (bytes += entry.Length) > 4L * 1024 * 1024 * 1024)
                throw new InvalidDataException("Unsafe runtime archive.");
            Lopata.Updates.SafeUpdatePath.RejectLinks(target);
            if (entry.Name.Length == 0) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, false);
        }
    }
    public void Dispose() => _downloads.Dispose();
}
