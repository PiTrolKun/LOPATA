using AIHub.Models;

namespace AIHub.Services;

public interface IMusicPreparation : IDisposable
{
    bool LyricsComponentsReady => true;
    string Variation { get => MusicComponentCatalog.ModelId; set { } }
    int MaximumParallelConnections { get; set; }
    Task<IReadOnlyList<ManagedModelArtifactCard>> CheckAsync(string root,
        IProgress<ManagedModelDownloadProgress>? progress, CancellationToken token);
    Task<IReadOnlyList<ManagedModelArtifactCard>> PrepareAsync(string root, bool download,
        IProgress<ManagedModelDownloadProgress>? progress, CancellationToken token);
}

public sealed class MusicPreparationService : IMusicPreparation
{
    public bool LyricsComponentsReady => _lyricsVerified && MusicLyricsComponentCatalog.IsAvailable(new ComponentManager());
    private bool _lyricsVerified;
    public string Variation { get; set; } = MusicComponentCatalog.ModelId;
    private readonly ManagedModelLibraryStore _store;
    private readonly ManagedModelAcquisitionService _downloads;
    public MusicPreparationService(ManagedModelLibraryStore? store = null)
    { _store = store ?? new(); _downloads = new(_store); }
    public int MaximumParallelConnections
    { get => _downloads.MaximumParallelConnections; set => _downloads.MaximumParallelConnections = value; }

    private IReadOnlyList<ManagedModelArtifactCard> Register(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new InvalidOperationException("Music.StorageRequired");
        return MusicModelVariants.Cards(root, Variation).Select(c => _store.Upsert(c)).ToArray();
    }

    public async Task<IReadOnlyList<ManagedModelArtifactCard>> CheckAsync(string root,
        IProgress<ManagedModelDownloadProgress>? progress, CancellationToken token)
    {
        _lyricsVerified = false;
        if (MusicLyricsComponentCatalog.IsAvailable(new ComponentManager()))
        {
            try { await MusicLyricsComponentCatalog.VerifyAsync(new ComponentManager(), token); _lyricsVerified = true; }
            catch (System.IO.IOException) { /* The preparation page offers a repair download. Execution never bypasses verification. */ }
        }
        if (Variation == MusicStudioRuntime.Variation) await MusicStudioRuntime.VerifyAsync(token);
        if (MusicDiffRhythmCatalog.IsDiff(Variation)) await MusicDiffRhythmSource.VerifyAsync(token);
        if (MusicAceCatalog.IsAce(Variation)) await MusicAceSource.VerifyAsync(token);
        var result = new List<ManagedModelArtifactCard>();
        foreach (var card in Register(root))
        {
            token.ThrowIfCancellationRequested();
            result.Add(await _downloads.VerifyAsync(card.ModelArtifactId, progress, token));
        }
        return result;
    }

    public async Task<IReadOnlyList<ManagedModelArtifactCard>> PrepareAsync(string root, bool download,
        IProgress<ManagedModelDownloadProgress>? progress, CancellationToken token)
    {
        var cards = Register(root);
        await ComponentLicenseGate.EnsureAsync(MusicModelVariants.Components(Variation), token);
        _lyricsVerified = false;
        await MusicLyricsComponentCatalog.PrepareAsync(download, new Progress<ComponentDownloadProgress>(p => progress?.Report(
            new(p.ComponentId, ComponentCatalog.Find(p.ComponentId)?.Name ?? p.ComponentId,
                p.DownloadedBytes, p.TotalBytes, p.BytesPerSecond, p.Stage))), token);
        _lyricsVerified = true;
        if (MusicDiffRhythmCatalog.IsDiff(Variation)) await MusicDiffRhythmSource.VerifyAsync(token);
        if (MusicAceCatalog.IsAce(Variation)) await MusicAceSource.VerifyAsync(token);
        if (Variation == MusicStudioRuntime.Variation) await MusicStudioRuntime.VerifyAsync(token);
        var result = new List<ManagedModelArtifactCard>();
        foreach (var card in cards)
        {
            token.ThrowIfCancellationRequested();
            var verified = await _downloads.VerifyAsync(card.ModelArtifactId, progress, token);
            if (verified.Status != ManagedModelStatuses.Installed && download)
                verified = await _downloads.DownloadAsync(card.ModelArtifactId, progress, token);
            result.Add(verified);
        }
        token.ThrowIfCancellationRequested();
        return result;
    }
    public void Dispose() => _downloads.Dispose();
}
