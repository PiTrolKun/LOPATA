using System.IO;
using AIHub.Models;
using AIHub.Services.VideoPreparation;

namespace AIHub.Services.SongClip;

internal sealed record SongClipRemovalPlan(SongClipFile[] Remove, SongClipFile[] Keep, string[] ProtectedBy)
{
    internal string[] Affected {get;init;}=[];
    internal string[] Paths => Remove.SelectMany(f=>new[]{f.Path}.Concat(SegmentedModelFileDownloader.GetPartialArtifactPaths(f.Path)))
        .Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    internal long Bytes => Paths.Sum(path=>new FileInfo(path).Length);
}

internal static class SongClipComponentRemoval
{
    private static ManagedModelArtifactCard[] Registered(string root,ManagedModelLibraryStore? store)
    {
        var boundary=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        return (store??new ManagedModelLibraryStore()).LoadAll().Where(c=>Path.IsPathFullyQualified(c.InstallDirectory)
            &&Path.GetFullPath(c.InstallDirectory).StartsWith(boundary,StringComparison.OrdinalIgnoreCase)).ToArray();
    }
    private static bool HasArtifacts(SongClipFile f)=>File.Exists(f.Path)||SegmentedModelFileDownloader.GetPartialArtifactPaths(f.Path).Any(File.Exists);
    internal static SongClipRemovalPlan Plan(string root,string[] variantIds,ManagedModelLibraryStore? store=null)
    {
        var selected=variantIds.ToHashSet(StringComparer.Ordinal);
        var targets=SongClipVariantCatalog.Files(variantIds.SelectMany(id=>SongClipVariantCatalog.Cards(id,root)));
        var targetPaths=targets.Select(f=>f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var retained=SongClipVariantCatalog.Variants.Where(v=>v.Available&&!selected.Contains(v.Id))
            .Where(v=>SongClipVariantCatalog.DescribeFiles(SongClipVariantCatalog.Cards(v.Id,root)).All(f=>File.Exists(f.Path))).ToArray();
        var knownIds=SongClipVariantCatalog.Variants.Where(v=>v.Available||v.Retired).SelectMany(v=>SongClipVariantCatalog.Cards(v.Id,root))
            .Select(c=>c.ModelArtifactId).ToHashSet(StringComparer.Ordinal);
        var foreign=Registered(root,store).Where(c=>!knownIds.Contains(c.ModelArtifactId)||c.Consumers.Any(c=>c.Id!="song_clip"))
            .Select(c=>(Card:c,Files:SongClipVariantCatalog.DescribeFiles([c]).Where(f=>targetPaths.Contains(f.Path)).ToArray()))
            .Where(p=>p.Files.Length>0).ToArray();
        var protectedPaths=SongClipVariantCatalog.DescribeFiles(SongClipVariantCatalog.BaseCards("MiniMax H3",root)
            .Concat(retained.SelectMany(v=>SongClipVariantCatalog.Cards(v.Id,root))))
            .Select(f=>f.Path).Concat(foreign.SelectMany(p=>p.Files.Select(f=>f.Path))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new(targets.Where(f=>!protectedPaths.Contains(f.Path)&&HasArtifacts(f)).ToArray(),
            targets.Where(f=>protectedPaths.Contains(f.Path)&&File.Exists(f.Path)).ToArray(),
            ["MiniMax H3 · Video Workshop",..retained.Select(v=>v.Title),..foreign.SelectMany(p=>p.Card.Consumers.Select(c=>c.DisplayName)).Distinct()]);
    }
    // Explicit base deletion is a separate user action. It can remove shared H3 files
    // after the confirmation names affected scenarios; user/system-owned aliases remain protected.
    internal static SongClipRemovalPlan PlanBase(string root,ManagedModelLibraryStore? store=null)
    {
        var targets=SongClipVariantCatalog.Files(SongClipVariantCatalog.BaseCards("MiniMax H3",root));
        var targetPaths=targets.Select(f=>f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var aliases=Registered(root,store).Select(c=>(Card:c,Files:SongClipVariantCatalog.DescribeFiles([c])
            .Where(f=>targetPaths.Contains(f.Path)).ToArray())).Where(p=>p.Files.Length>0).ToArray();
        var protectedPaths=aliases.Where(p=>p.Card.IsSystem||!p.Card.IsManaged||p.Card.Origin!=ManagedModelOrigins.PredefinedScenario)
            .SelectMany(p=>p.Files.Select(f=>f.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new(targets.Where(f=>!protectedPaths.Contains(f.Path)&&HasArtifacts(f)).ToArray(),
            targets.Where(f=>protectedPaths.Contains(f.Path)&&File.Exists(f.Path)).ToArray(),
            aliases.Where(p=>p.Files.Any(f=>protectedPaths.Contains(f.Path))).Select(p=>p.Card.DisplayName).Distinct().ToArray()) {
            Affected=new[]{"Video Workshop (H3)"}.Concat(SongClipVariantCatalog.Variants.Where(v=>(v.Available||v.Retired)
                &&SongClipVariantCatalog.DescribeFiles(SongClipVariantCatalog.Cards(v.Id,root)).Any(f=>targetPaths.Contains(f.Path)))
                .Select(v=>v.Title)).Concat(aliases.SelectMany(p=>p.Card.Consumers.Select(c=>c.DisplayName)))
                .Distinct(StringComparer.Ordinal).ToArray()
        };
    }
    internal static void Execute(SongClipRemovalPlan plan, ManagedModelLibraryStore store, IModelUsageGuard guard)
    {
        // Retire resident models and check the global operation gate before calling.
        foreach(var file in plan.Remove) {
            Lopata.Updates.SafeUpdatePath.ValidateRelative(file.File.RelativePath);
            if(!StringComparer.OrdinalIgnoreCase.Equals(file.Path,RuntimeFileAudit.Contained(file.Card.InstallDirectory,file.File.RelativePath)))
                throw new InvalidDataException("Component removal inventory changed.");
            var registered=store.Load(file.Card.ModelArtifactId);
            if(guard.IsActive(file.Card.ModelArtifactId)||registered?.IsSystem==true||registered?.Status==ManagedModelStatuses.InUse
                || registered is {IsManaged:false} || (registered is not null&&registered.Origin!=ManagedModelOrigins.PredefinedScenario))
                throw new InvalidOperationException("Component is protected or in use.");
        }
        foreach (var group in plan.Remove.GroupBy(f=>f.Card.ModelArtifactId)) {
            var card=group.First().Card; store.Upsert(card);
            try {new ManagedModelRemovalService(store,guard).RemoveScenarioFiles(card.ModelArtifactId,
                group.Select(f=>f.File.RelativePath).ToArray(),includePartialFiles:true);}
            finally {
                var derived=card.ModelArtifactId switch {
                    "song-clip-glitch-source"=>"source", "song-clip-glitch-libraries"=>"libraries",
                    H3MusicComponents.SourceId=>"h3-motion", H3MontageComponents.SourceId=>"h3-any-song",
                    GeekatplayComponents.SourceId=>"geekatplay", InfiniteTalkComponents.SourceId=>"infinitetalk/source",
                    InfiniteTalkComponents.ExtrasId=>"infinitetalk/libraries", _=>null};
                if(card.ModelArtifactId.StartsWith("video-h3-",StringComparison.Ordinal))
                    ComponentAudit.Invalidate(Path.Combine(AppDataPaths.BaseDirectory,"VideoPreparation","H3"));
                if(derived is not null)ComponentAudit.Invalidate(Path.Combine(AppDataPaths.BaseDirectory,"SongClip","Runtime",derived,card.Revision));
            }
        }
    }
}
