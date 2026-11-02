using System.IO;
using System.Security.Cryptography;

namespace AIHub.Services.SongClip;

internal static class SongClipImport
{
    internal static async Task<SongClipProject> AddSongAsync(SongClipStore store,SongClipProject current,string source,CancellationToken token)
    {
        using var input=File.OpenRead(source);
        var hash=Convert.ToHexStringLower(await SHA256.HashDataAsync(input,token));
        var same=current.Song.Length>0&&Path.GetFileNameWithoutExtension(current.Song)==hash;
        var next=current.Song.Length==0||same?current:new SongClipProject {
            Name=Path.GetFileNameWithoutExtension(source),Model=current.Model,
            Settings=current.Settings with{Prompt=new SongClipSettings().Prompt}
        };
        var owned=await store.ImportAsync(next.Id,source,token);
        token.ThrowIfCancellationRequested();
        next.Song=owned;
        if(next.Name.Length==0)next.Name=Path.GetFileNameWithoutExtension(source);
        return next;
    }
}
