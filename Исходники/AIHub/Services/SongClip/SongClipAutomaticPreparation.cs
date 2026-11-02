using System.IO;
using AIHub.Models;

namespace AIHub.Services.SongClip;

/// <summary>Checks the immutable attempt before it enters the background queue.</summary>
internal static class SongClipAutomaticPreparation
{
    internal static bool NeedsDownload(IEnumerable<ManagedModelArtifactCard> cards) =>
        SongClipVariantCatalog.Files(cards).Any(file => !File.Exists(file.Path)
            || new FileInfo(file.Path).Length != file.File.SizeBytes);

    internal static bool PrepareIfNeeded(SongClipAttempt attempt, Func<SongClipVariant, bool> prepare)
    {
        var variant = SongClipVariantCatalog.Current(attempt.Model, attempt.Settings.Motion.Mode);
        if (variant.Model != attempt.Model || !variant.Available || variant.Retired)
            throw new InvalidOperationException("SongClip.Pending");
        var cards = SongClipVariantCatalog.Cards(variant.Id, attempt.ModelsRoot);
        if (cards.Length == 0) throw new InvalidDataException("SongClip.DownloadRequired");
        return !NeedsDownload(cards) || prepare(variant);
    }
}
