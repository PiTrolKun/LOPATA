using System.IO;
using System.Net.Http;
using AIHub.Models;

namespace AIHub.Services.VideoPreparation;

internal interface IVideoVacePreparation : IDisposable
{
    IReadOnlyList<ManagedModelArtifactCard> Cards { get; }
    int ParallelConnections { get; set; }
    IReadOnlyList<VideoH3FilePresence> Inspect();
    Task<bool> PrepareAsync(bool download, IProgress<ManagedModelDownloadProgress>? progress, CancellationToken token);
}

/// <summary>Files only: shared resumable acquisition, license consent and component audit; no runtime execution.</summary>
internal sealed class VideoVacePreparation : IVideoVacePreparation
{
    private readonly ManagedModelLibraryStore _store;
    private readonly ManagedModelAcquisitionService _acquisition;
    public IReadOnlyList<ManagedModelArtifactCard> Cards { get; }
    public int ParallelConnections { get => _acquisition.MaximumParallelConnections; set => _acquisition.MaximumParallelConnections = value; }

    internal VideoVacePreparation(string modelsRoot, ManagedModelLibraryStore? store = null, HttpClient? http = null,
        IReadOnlyList<ManagedModelArtifactCard>? cards = null, long segmentMinimumBytes = 64L * 1024 * 1024)
    {
        _store = store ?? new(); Cards = cards ?? VideoVaceRuntime.Cards(modelsRoot);
        _acquisition = new(_store, http, segmentMinimumBytes);
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
            // Restore authoritative pins instead of trusting mutable model-library coordinates.
            _store.Upsert(expected);
            ManagedModelArtifactCard result;
            if (expected.Files.Any(f => f.IsRequired && !File.Exists(Path.Combine(expected.InstallDirectory, f.RelativePath))))
            {
                // Unacquired files are an ordinary preparation state, not a component failure.
                expected.Status = ManagedModelStatuses.FilesRemoved;
                result = _store.Upsert(expected);
            }
            else result = await _acquisition.VerifyAsync(expected.ModelArtifactId, progress, token);
            if (result.Status != ManagedModelStatuses.Installed && download)
                result = await _acquisition.DownloadAsync(expected.ModelArtifactId, progress, token);
            if (result.Status == ManagedModelStatuses.Installed)
                progress?.Report(new(expected.ModelArtifactId, "", expected.TotalBytes, expected.TotalBytes, 0, "verified"));
            ready &= result.Status == ManagedModelStatuses.Installed;
        }
        token.ThrowIfCancellationRequested();
        return ready;
    }

    public void Dispose() => _acquisition.Dispose();
}
