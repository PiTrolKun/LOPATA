using System.IO;
using AIHub.Models;

namespace AIHub.Services.SongClip;

internal sealed record SongClipPickerRow(SongClipPresence Presence,long ExtraBytes,bool HasArtifacts);
internal sealed record SongClipPickerInventory(IReadOnlyDictionary<string,SongClipPickerRow> Variants,
    SongClipPickerRow H3Base,IReadOnlyDictionary<string,long> FamilyBytes)
{
    // A per-window, read-only snapshot. No hashes, library scans, removal plans or runtime warmup.
    internal static SongClipPickerInventory Scan(string root,CancellationToken token)
    {
        var choices=SongClipVariantCatalog.VisibleVariants.ToArray();
        var cards=choices.ToDictionary(v=>v.Id,v=>SongClipVariantCatalog.Cards(v.Id,root));
        var files=cards.ToDictionary(p=>p.Key,p=>SongClipVariantCatalog.DescribeFiles(p.Value));
        var baseFiles=SongClipVariantCatalog.DescribeFiles(SongClipVariantCatalog.BaseCards("MiniMax H3",root));
        var basePaths=baseFiles.Select(f=>f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var states=new Dictionary<string,(bool Exists,long Partial)>(StringComparer.OrdinalIgnoreCase);
        foreach(var file in files.Values.SelectMany(f=>f).Concat(baseFiles).DistinctBy(f=>f.Path,StringComparer.OrdinalIgnoreCase)) {
            token.ThrowIfCancellationRequested();var exists=File.Exists(file.Path);
            var partial=exists?0:SegmentedModelFileDownloader.GetPartialArtifactPaths(file.Path).Where(File.Exists).Sum(p=>new FileInfo(p).Length);
            states[file.Path]=(exists,partial);
        }
        SongClipPickerRow Summarize(SongClipFile[] list,long extra=0) {
            var present=list.Where(f=>states[f.Path].Exists).Sum(f=>f.File.SizeBytes);
            var missing=list.Where(f=>!states[f.Path].Exists).Sum(f=>Math.Max(0,f.File.SizeBytes-Math.Min(f.File.SizeBytes,states[f.Path].Partial)));
            return new(new(list.Sum(f=>f.File.SizeBytes),missing,present,list.Length>0&&list.All(f=>states[f.Path].Exists)),extra,
                list.Any(f=>states[f.Path].Exists||states[f.Path].Partial>0));
        }
        var variants=choices.ToDictionary(v=>v.Id,v=>Summarize(files[v.Id],v.Family=="MiniMax H3"
            ?files[v.Id].Where(f=>!basePaths.Contains(f.Path)).Sum(f=>f.File.SizeBytes):0));
        var families=choices.GroupBy(v=>v.Family).ToDictionary(g=>g.Key,g=>g.SelectMany(v=>files[v.Id])
            .DistinctBy(f=>f.Path,StringComparer.OrdinalIgnoreCase).Sum(f=>f.File.SizeBytes));
        return new(variants,Summarize(baseFiles),families);
    }
}
