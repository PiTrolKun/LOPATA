using System.IO;
using System.Security.Cryptography;
using AIHub.Models;

namespace AIHub.Services.Books.Smart;

/// <summary>Selected model acquisition through the existing library, license gate and segmented downloader.</summary>
public sealed class BookSmartPreparationService : IDisposable
{
    private readonly string _modelsRoot;
    private readonly bool _hasImages;
    private readonly Func<string, string> _localize;
    private readonly Func<BookSmartSelection, string, IReadOnlyList<ManagedModelArtifactCard>> _ocrCards;
    private readonly Func<BookSmartSelection, bool, IProgress<string>, CancellationToken, Task<BookSmartPreparationItem[]>>? _runtimePreparation;
    private readonly ManagedModelLibraryStore _store;
    private readonly ManagedModelAcquisitionService _acquisition;
    private readonly bool _ownsAcquisition;
    private readonly SemaphoreSlim _gate = new(1);

    public int MaximumParallelConnections { get => _acquisition.MaximumParallelConnections; set => _acquisition.MaximumParallelConnections = value; }

    public BookSmartPreparationService(string modelsRoot, bool hasImages, Func<string, string> localize,
        Func<BookSmartSelection, string, IReadOnlyList<ManagedModelArtifactCard>> ocrCards,
        Func<BookSmartSelection, bool, IProgress<string>, CancellationToken, Task<BookSmartPreparationItem[]>>? runtimePreparation = null,
        ManagedModelLibraryStore? store = null, ManagedModelAcquisitionService? acquisition = null)
    {
        _modelsRoot = modelsRoot; _hasImages = hasImages; _localize = localize; _ocrCards = ocrCards;
        _runtimePreparation = runtimePreparation; _store = store ?? new();
        _acquisition = acquisition ?? new(_store); _ownsAcquisition = acquisition is null;
    }

    public async Task<BookSmartReadiness> PrepareAsync(BookSmartSelection selection, bool download,
        IProgress<string> progress, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var states = new List<BookSmartPreparationItem>();
            foreach (var definition in CreateCards(selection))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var card = Register(definition);
                progress.Report(string.Format(L("CheckFile"), card.DisplayName));
                var transfer = new InlineProgress<ManagedModelDownloadProgress>(value => progress.Report(
                    value.Stage == "verifying" ? string.Format(L("CheckFile"), card.DisplayName)
                    : string.Format(L("Transfer"), card.DisplayName, value.DownloadedBytes / 1048576d, value.TotalBytes / 1048576d)));
                card = await _acquisition.VerifyAsync(card.ModelArtifactId, transfer, cancellationToken);
                if (!FilesReady(card) && download)
                    card = await _acquisition.DownloadAsync(card.ModelArtifactId, transfer, cancellationToken);
                var ready = FilesReady(card);
                states.Add(new(card.DisplayName, L(ready ? "FilesVerified" : "Missing"),
                    ready ? 0 : await MissingBytesAsync(card, cancellationToken), ready));
            }
            if (_runtimePreparation is not null)
            {
                var runtime = await _runtimePreparation(selection, download, progress, cancellationToken);
                if (runtime.Length == 0) states.Add(new(L("Runtime"), L("RuntimeMissing"), 0, false));
                else states.AddRange(runtime);
            }
            else states.Add(new(L("Runtime"), L("RuntimeMissing"), 0, false));
            cancellationToken.ThrowIfCancellationRequested();
            return new(states.ToArray());
        }
        finally { _gate.Release(); }
    }

    internal IReadOnlyList<ManagedModelArtifactCard> CreateCards(BookSmartSelection selection, bool? hasImages = null)
    {
        if (string.IsNullOrWhiteSpace(_modelsRoot)) throw new InvalidOperationException(L("StorageMissing"));
        if (selection.OcrId is not ("paddle" or "ovis") || selection.VisionId is not ("alpha" or "beta"))
            throw new ArgumentException("Unknown book smart model selection.", nameof(selection));
        var ocr = _ocrCards(selection, _modelsRoot);
        if (ocr.Count == 0) throw new InvalidOperationException(L("OcrMissing"));
        List<ManagedModelArtifactCard> cards = [ManagedModelCatalog.CreateCore(_modelsRoot), .. ocr];
        if (hasImages ?? _hasImages) cards.Add(selection.VisionId == "beta" ? ManagedModelCatalog.CreateOmniBeta(_modelsRoot) : ManagedModelCatalog.CreateOmniAlpha(_modelsRoot));
        return cards.DistinctBy(card => card.ModelArtifactId, StringComparer.Ordinal).ToArray();
    }

    // Cheap session reuse: trust only successful verification stamps, never hash GBs or probe Python again here.
    internal string[]? VerifiedPaths(BookSmartSelection selection, bool hasImages)
    {
        var paths = new List<string>();
        foreach (var definition in CreateCards(selection, hasImages))
        {
            var card = _store.Load(definition.ModelArtifactId);
            if (card is null || !SameCoordinates(card, definition) || !FilesReady(card)) return null;
            var root = Path.GetFullPath(card.InstallDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (var file in card.Files.Where(file => file.IsRequired))
            {
                var path = Path.GetFullPath(Path.Combine(root, file.RelativePath));
                if (Path.IsPathRooted(file.RelativePath) || !path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
                var info = new FileInfo(path);
                if (!info.Exists || info.Length != file.SizeBytes || file.VerifiedSizeBytes != info.Length
                    || file.VerifiedLastWriteTimeUtc != info.LastWriteTimeUtc) return null;
                paths.Add(path);
            }
        }
        return paths.ToArray();
    }

    private ManagedModelArtifactCard Register(ManagedModelArtifactCard definition)
    {
        var existing = _store.Load(definition.ModelArtifactId);
        // Reuse an already registered installation, preserving status and verified stamps until a real check updates them.
        var card = existing is not null && SameCoordinates(existing, definition) ? existing : definition;
        if (!card.Consumers.Any(consumer => consumer.Id == "book-smart"))
            card.Consumers.Add(new() { Id = "book-smart", DisplayName = "LOPATA · Book smart mode", Kind = "scenario_bundle" });
        return _store.Upsert(card);
    }

    private static bool SameCoordinates(ManagedModelArtifactCard left, ManagedModelArtifactCard right) =>
        left.IsManaged && !string.IsNullOrWhiteSpace(left.ModelsRoot) && !string.IsNullOrWhiteSpace(left.InstallDirectory)
        && left.RepositoryId == right.RepositoryId && left.Revision == right.Revision && left.Files.Count == right.Files.Count
        && right.Files.All(file => left.Files.Any(previous => previous.RelativePath == file.RelativePath
            && previous.SizeBytes == file.SizeBytes && previous.Sha256.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)));

    private static bool FilesReady(ManagedModelArtifactCard card) =>
        card.Files.Any(file => file.IsRequired) && card.Status is ManagedModelStatuses.Installed or ManagedModelStatuses.NeedsVerification;

    private static async Task<long> MissingBytesAsync(ManagedModelArtifactCard card, CancellationToken token)
    {
        var root = Path.GetFullPath(card.InstallDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        long missing = 0;
        foreach (var file in card.Files.Where(file => file.IsRequired))
        {
            token.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(Path.Combine(root, file.RelativePath));
            if (Path.IsPathRooted(file.RelativePath) || !path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The model manifest file path escapes its installation.");
            var info = new FileInfo(path); var valid = false;
            if (info.Exists && info.Length == file.SizeBytes)
            {
                if (file.VerifiedSizeBytes == info.Length && file.VerifiedLastWriteTimeUtc == info.LastWriteTimeUtc) valid = true;
                else
                {
                    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    valid = Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase);
                }
            }
            if (!valid) missing += Math.Max(0, file.SizeBytes - SegmentedModelFileDownloader.GetPartialStoredBytes(path, file.SizeBytes));
        }
        return missing;
    }

    private string L(string key) => _localize("BookConverter.SmartPreparation." + key);
    public void Dispose() { if (_ownsAcquisition) _acquisition.Dispose(); _gate.Dispose(); }
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    { public void Report(T value) => report(value); }
}
