using System.IO;
using System.Net.Http;
using AIHub.Models;

namespace AIHub.Services.VideoPreparation;

internal interface IVideoLtxPreparation : IDisposable
{
    IReadOnlyList<ManagedModelArtifactCard> Cards { get; }
    int ParallelConnections { get; set; }
    string AccessToken { set; }
    IReadOnlyList<VideoH3FilePresence> Inspect();
    Task<bool> PrepareAsync(bool download, IProgress<ManagedModelDownloadProgress>? progress, CancellationToken token);
}

/// <summary>Acquires files only, using shared licensing, audit and segmented download contracts.</summary>
internal sealed class VideoLtxPreparation : IVideoLtxPreparation
{
    private readonly ManagedModelLibraryStore _store;
    private readonly ManagedModelAcquisitionService _acquisition;
    private readonly HttpClient _http;
    private string _accessToken = "";
    public IReadOnlyList<ManagedModelArtifactCard> Cards { get; }
    public int ParallelConnections { get => _acquisition.MaximumParallelConnections; set => _acquisition.MaximumParallelConnections = value; }
    public string AccessToken
    {
        set
        {
            var candidate = value?.Trim() ?? "";
            if (candidate.Length > 512 || candidate.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')))
                throw new InvalidOperationException("VideoPrep.LtxAccessRequired");
            _accessToken = candidate;
        }
    }

    internal VideoLtxPreparation(string modelsRoot, ManagedModelLibraryStore? store = null, HttpClient? http = null,
        IReadOnlyList<ManagedModelArtifactCard>? cards = null, long segmentMinimumBytes = 64L * 1024 * 1024)
    {
        _store = store ?? new(); Cards = cards ?? VideoLtxCatalog.Cards(modelsRoot);
        foreach (var card in Cards)
        {
            // Do not trust hashes or URLs from the mutable model-library record.
            foreach (var file in card.Files) file.Sha256 = "";
            VideoLtxMetadata.Load(card);
        }
        _http = new HttpClient(new VideoLtxTransport(() => _accessToken, http));
        _acquisition = new(_store, _http, segmentMinimumBytes);
    }

    public IReadOnlyList<VideoH3FilePresence> Inspect() => VideoH3DownloadState.Inspect(Cards);

    public async Task<bool> PrepareAsync(bool download, IProgress<ManagedModelDownloadProgress>? progress, CancellationToken token)
    {
        using var audit = ComponentAudit.CaptureOperation();
        token.ThrowIfCancellationRequested();
        if (download) await ComponentLicenseGate.EnsureAsync(Cards.Select(c => c.ModelArtifactId).ToArray(), token);
        var ready = true;
        foreach (var expected in Cards)
        {
            token.ThrowIfCancellationRequested();
            if (!VideoLtxMetadata.Complete(expected))
            {
                // Opening the page performs local inspection only. A supplied token is explicit verification.
                if (_accessToken.Length == 0)
                {
                    if (download) throw new InvalidOperationException("VideoPrep.LtxAccessRequired");
                    ready = false; continue;
                }
                progress?.Report(new(expected.ModelArtifactId, "", 0, expected.TotalBytes, 0, "metadata"));
                await VideoLtxMetadata.ResolveAsync(expected, _http, token);
            }
            token.ThrowIfCancellationRequested();
            _store.Upsert(expected);
            ManagedModelArtifactCard result;
            if (expected.Files.Any(f => f.IsRequired && !File.Exists(Path.Combine(expected.InstallDirectory, f.RelativePath))))
            {
                expected.Status = ManagedModelStatuses.FilesRemoved;
                result = _store.Upsert(expected);
            }
            else result = await _acquisition.VerifyAsync(expected.ModelArtifactId, progress, token);
            if (result.Status != ManagedModelStatuses.Installed && download)
            {
                if (_accessToken.Length == 0) throw new InvalidOperationException("VideoPrep.LtxAccessRequired");
                result = await _acquisition.DownloadAsync(expected.ModelArtifactId, progress, token);
            }
            if (result.Status == ManagedModelStatuses.Installed)
                progress?.Report(new(expected.ModelArtifactId, "", expected.TotalBytes, expected.TotalBytes, 0, "verified"));
            ready &= result.Status == ManagedModelStatuses.Installed;
        }
        token.ThrowIfCancellationRequested();
        return ready;
    }

    public void Dispose() { _accessToken = ""; _acquisition.Dispose(); _http.Dispose(); }
}
