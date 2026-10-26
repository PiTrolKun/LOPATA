using System.IO;
using System.Net.Http;
using AIHub.Models;

namespace AIHub.Services.VideoEditor;

internal sealed record VideoModelNeed(string Action, string[] Voices, string? ModelsRoot = null);
internal sealed record VideoPreparedModels(string ModelsRoot);

/// <summary>Uses the same managed cards, receipts, licences and segmented acquisition as the parent scenarios.</summary>
internal sealed class VideoModelPreparation : IDisposable
{
    private readonly ManagedModelLibraryStore _store;
    private readonly ManagedModelAcquisitionService _downloads;
    private readonly Func<string, VideoModelNeed, IReadOnlyList<ManagedModelArtifactCard>> _cards;
    internal VideoModelPreparation(ManagedModelLibraryStore? store = null, HttpClient? client = null,
        Func<string, VideoModelNeed, IReadOnlyList<ManagedModelArtifactCard>>? cards = null)
    { _store = store ?? new(); _downloads = new(_store, client); _cards = cards ?? Cards; }
    internal int ParallelConnections { get => _downloads.MaximumParallelConnections; set => _downloads.MaximumParallelConnections = value; }
    internal static IReadOnlyList<ManagedModelArtifactCard> Cards(string root, VideoModelNeed need)
    {
        if (need.Action == "music") return MusicModelVariants.Cards(root, MusicStudioRuntime.Variation);
        if (need.Action == "analyze") return [ManagedModelCatalog.CreateOmniGamma(root)];
        if (need.Action != "speech") return [];
        return need.Voices.Distinct().SelectMany(id => {
            if (VideoNarratorCatalog.Get(id).Blocked is not null) throw new InvalidDataException("VideoEditor.VoiceUnavailable");
            return new[] { VideoNarratorCatalog.Card(root, id), VideoNarratorRuntime.Card(root, id) };
        }).DistinctBy(c => c.ModelArtifactId).ToArray();
    }
    internal async Task<bool> PrepareAsync(string root, VideoModelNeed need, bool download, IProgress<ManagedModelDownloadProgress>? progress, CancellationToken token)
    {
        using var audit = ComponentAudit.CaptureOperation();
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root)) throw new InvalidOperationException("VideoEditor.StorageRequired");
        var cards = _cards(root, need);
        var additional = need.Action == "speech" && need.Voices.Contains("kitten2")
            ? VideoNarratorCatalog.Pin("kitten2").AdditionalModels ?? [] : [];
        await ComponentLicenseGate.EnsureAsync(cards.Select(c => c.ModelArtifactId).Concat(additional.Select(m => m.Id)).ToArray(), token);
        var ready = true;
        foreach (var expected in cards) {
            var previous = _store.Load(expected.ModelArtifactId);
            // Preserve access to the old revision before Upsert replaces its card, including offline preparation.
            await VideoVoiceRuntimeUpgrade.ReuseAsync(previous, expected, token);
            _store.Upsert(previous is not null && previous.Revision == expected.Revision && (need.Action != "speech" || VideoNarratorCatalog.MatchesPin(previous, expected)) && Directory.Exists(previous.InstallDirectory)
                && (need.Action != "music" || previous.InstallDirectory.Equals(expected.InstallDirectory, StringComparison.OrdinalIgnoreCase)) ? previous : expected);
            var checkedCard = await _downloads.VerifyAsync(expected.ModelArtifactId, progress, token);
            if (checkedCard.Status != ManagedModelStatuses.Installed && download) checkedCard = await _downloads.DownloadAsync(expected.ModelArtifactId, progress, token);
            ready &= checkedCard.Status == ManagedModelStatuses.Installed;
        }
        if (ready && need.Action == "speech")
        {
            foreach (var runtime in cards.Where(c => c.ModelArtifactId.StartsWith("video-narrator-runtime-", StringComparison.Ordinal)))
            {
                try { await VideoNarratorRuntime.PrepareAsync(_store.Load(runtime.ModelArtifactId)!, token, reinstall: download); }
                catch (IOException) when (!download) { ready = false; }
            }
            foreach (var vosk in cards.Where(c => c.ModelArtifactId == "video-narrator-vosk010"))
            {
                try { await VideoVoiceZipArchive.PrepareVoskAsync(_store.Load(vosk.ModelArtifactId)!, token, reinstall: download); }
                catch (IOException) when (!download) { ready = false; }
            }
        }
        token.ThrowIfCancellationRequested(); return ready;
    }
    public void Dispose() => _downloads.Dispose();
}
