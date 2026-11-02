using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.SongClip;

internal static class InfiniteTalkBinding
{
    internal static readonly (string File,string Resource)[] Scripts = [
        ("song_clip_infinitetalk.py","AIHub.SongClip.InfiniteWorker"),
        ("song_clip_infinitetalk_native.py","AIHub.SongClip.InfiniteNative"),
        ("song_clip_infinitetalk_state.py","AIHub.SongClip.InfiniteState"),
        ("song_clip_motion_state.py","AIHub.SongClip.MotionState"), ("h3_memory.py","AIHub.H3Memory")];
    internal static PinnedTreeFile[] AdapterFiles() => Scripts.Select(item=>{
        using var stream=typeof(InfiniteTalkBinding).Assembly.GetManifestResourceStream(item.Resource)
            ?? throw new InvalidDataException("Missing InfiniteTalk adapter resource.");
        return new PinnedTreeFile(item.File,stream.Length,Convert.ToHexString(SHA256.HashData(stream)));
    }).ToArray();
    internal static async Task EnsureAsync(SongClipAttempt attempt,SongClipStore store,string job,
        IReadOnlyList<ManagedModelArtifactCard> cards,CancellationToken token)
    {
        var identity=JsonSerializer.SerializeToElement(new {schema=1,attempt,
            song=await H3MontageBinding.HashAsync(store.Resolve(attempt.ProjectId,attempt.Song),token),
            references=await SongClipReferences.HashAsync(attempt,store,token),scripts=AdapterFiles(),
            pins=cards.OrderBy(c=>c.ModelArtifactId,StringComparer.Ordinal).Select(c=>new {c.ModelArtifactId,c.Revision,
                files=c.Files.OrderBy(f=>f.RelativePath,StringComparer.Ordinal).Select(f=>new {f.RelativePath,f.SizeBytes,f.Sha256,f.IsRequired})})});
        var path=Path.Combine(job,"infinitetalk-input-binding.json");
        if(File.Exists(path)) {
            using var saved=JsonDocument.Parse(await File.ReadAllTextAsync(path,token));
            if(!JsonElement.DeepEquals(saved.RootElement,identity))throw new InvalidDataException("SongClip.VisualPlanMismatch");
        }
        else {
            if(Directory.EnumerateFileSystemEntries(job).Any(p=>Path.GetFileName(p) is not ("snapshot.json" or "elapsed.json")))
                throw new InvalidDataException("SongClip.VisualPlanMismatch");
            token.ThrowIfCancellationRequested();SongClipStore.Atomic(path,identity);
        }
    }
}
