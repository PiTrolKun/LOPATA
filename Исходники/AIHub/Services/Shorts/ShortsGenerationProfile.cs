using AIHub.Models;
using AIHub.Services.SongClip;
using AIHub.Services.VideoPreparation;

namespace AIHub.Services.Shorts;

internal readonly record struct ShortsGenerationProfile(bool FromSong, bool Deno, bool Accelerate)
{
    internal static ShortsGenerationProfile FromDraft(ShortsDraft draft) =>
        draft.FromSong ? new(true, true, true) : new(false, draft.UseDeno, draft.UseDeno && draft.AccelerateDeno);

    internal static ShortsGenerationProfile FromJob(ShortsJob job) =>
        job.FromSong
            ? new(true, job.Song is { } song && SongClipModes.IsDeno(song),
                job.Song is { } attempt && SongClipDeno.Accelerated(attempt))
            : new(false, job.Description?.Deno is not null, job.Description?.Deno?.Accelerate == true);

    internal ManagedModelArtifactCard[] Cards(string root)
    {
        if (FromSong) return SongClipVariantCatalog.Cards(Deno ? SongClipDeno.Mode : SongClipModes.Montage, root, Accelerate);
        var baseCards = VideoH3Catalog.Cards(root).Append(VideoH3RuntimeCatalog.Card(root));
        return Deno ? [.. baseCards, .. SongClipDenoComponents.Cards(root, Accelerate)] : [.. baseCards];
    }
}
