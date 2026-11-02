using System.IO;
using AIHub.Models;
using AIHub.Services.VideoPreparation;

namespace AIHub.Services.SongClip;

internal sealed record SongClipVariant(string Id, string Family, string Model, string? Mode, string Title, string Hint, bool Available = true, bool Retired = false);
internal sealed record SongClipFile(ManagedModelArtifactCard Card, ManagedModelArtifactFile File, string Path);
internal sealed record SongClipPresence(long Total, long Missing, long Present, bool Ready);

internal static class SongClipVariantCatalog
{
    internal static readonly SongClipVariant[] Variants = [
        new("glitchframe", "SDXL", "glitchframe", null, "Glitchframe", "PickerGlitch"),
        new("motion", "MiniMax H3", "h3_music_context", null, "Motion Context", "PickerMotion"),
        new("montage", "MiniMax H3", "h3_music_context", "montage", "Any Song", "PickerMontage"),
        new("geekatplay", "MiniMax H3", GeekatplayRunner.ModelId, null, "Geekatplay", "PickerGeek"),
        new("infinitetalk", "Wan2.1", "infinitetalk", null, "InfiniteTalk", "PickerInfinite", false, true),
        new("longcat_avatar_15", "LongCat-Video", "longcat_avatar_15", null, "LongCat Avatar 1.5", "PickerLongCat", false, true)];
    // Historical identities remain readable and maintainable without offering a retired pipeline.
    internal static IEnumerable<SongClipVariant> VisibleVariants => Variants.Where(v => !v.Retired);
    internal static bool IsRetiredModel(string model) => Variants.Any(v => v.Model == model && v.Retired);
    internal static bool CanGenerateModel(string model) => Variants.Any(v => v.Model == model && v.Available && !v.Retired);
    internal static SongClipVariant? AvailableFamily(string family) => VisibleVariants.FirstOrDefault(v => v.Available && v.Family == family);
    internal const string RetiredMessageKey = "SongClip.ModelRetired";
    internal static SongClipVariant Find(string id) => Variants.Single(v => v.Id == id);
    internal static SongClipVariant Current(string model, string? mode) => Variants.FirstOrDefault(v => v.Model == model
        && (model != "h3_music_context" || SongClipModes.IsMontage(mode) == SongClipModes.IsMontage(v.Mode))) ?? Variants[0];
    internal static ManagedModelArtifactCard[] Cards(string id, string root) => id switch {
        "glitchframe" => GlitchframeComponents.Cards(root),
        "motion" => H3MusicComponents.Cards(root).ToArray(),
        "montage" => H3MontageComponents.Cards(root).ToArray(),
        "geekatplay" => GeekatplayComponents.Cards(root),
        "infinitetalk" => InfiniteTalkComponents.Cards(root),
        _ => []
    };
    internal static ManagedModelArtifactCard[] BaseCards(string family, string root) => family == "MiniMax H3"
        ? [..VideoH3Catalog.Cards(root), VideoH3RuntimeCatalog.Card(root)] : [];
    internal static SongClipFile[] DescribeFiles(IEnumerable<ManagedModelArtifactCard> cards) => cards.SelectMany(c => c.Files
        .Select(f => new SongClipFile(c, f, DisplayPath(c.InstallDirectory,f.RelativePath))))
        .DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    private static string DisplayPath(string directory,string relative)
    {
        var root=Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        var path=Path.GetFullPath(Path.Combine(root,relative));
        if(Path.IsPathRooted(relative)||!path.StartsWith(root,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Unsafe component path.");
        return path;
    }
    internal static SongClipFile[] Files(IEnumerable<ManagedModelArtifactCard> cards)
    {
        var files=DescribeFiles(cards);
        foreach(var file in files)Lopata.Updates.SafeUpdatePath.RejectLinks(file.Path);
        return files;
    }
    internal static SongClipPresence Presence(IEnumerable<ManagedModelArtifactCard> cards)
    {
        var files = DescribeFiles(cards); long missing=0, present=0;
        foreach (var file in files) {
            if (System.IO.File.Exists(file.Path)) { present += file.File.SizeBytes; continue; }
            // Sizes here are a download estimate, not an integrity/audit decision.
            var partial = SegmentedModelFileDownloader.GetPartialArtifactPaths(file.Path)
                .Where(System.IO.File.Exists).Sum(p => new FileInfo(p).Length);
            missing += Math.Max(0, file.File.SizeBytes - Math.Min(file.File.SizeBytes, partial));
        }
        return new(files.Sum(f => f.File.SizeBytes), missing, present, files.Length > 0 && files.All(f => System.IO.File.Exists(f.Path)));
    }
    internal static ManagedModelArtifactCard[] GroupCards(string family, string root) => Union(Variants
        .Where(v => v.Available && v.Family == family).SelectMany(v => Cards(v.Id, root)));
    internal static ManagedModelArtifactCard[] Union(IEnumerable<ManagedModelArtifactCard> cards)
    {
        // Keep complete canonical manifests; omit a smaller alias fully covered by another card.
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return cards.DistinctBy(c => c.ModelArtifactId).OrderByDescending(c => c.TotalBytes).Where(card => {
            var own = DescribeFiles([card]).Select(f => f.Path).ToArray();
            if (own.All(paths.Contains)) return false;
            foreach (var path in own) paths.Add(path);
            return true;
        }).ToArray();
    }
    internal static long ExtraBytes(string id, string root)
    {
        var variant = Find(id);
        var shared = DescribeFiles(BaseCards(variant.Family, root)).Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return DescribeFiles(Cards(id, root)).Where(f => !shared.Contains(f.Path)).Sum(f => f.File.SizeBytes);
    }
}
