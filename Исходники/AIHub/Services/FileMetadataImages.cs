using System.IO;
using System.Security.Cryptography;
using AIHub.Models;

namespace AIHub.Services;

public static class FileMetadataImages
{
    public static async Task ExportAsync(FileMetadataPicture picture,string destination,IEnumerable<string> sourceFiles,CancellationToken token)
    {
        destination=Path.GetFullPath(destination);
        if(sourceFiles.Any(p=>Path.GetFullPath(p).Equals(destination,StringComparison.OrdinalIgnoreCase)))throw new IOException("Metadata.ExportSource");
        Lopata.Updates.SafeUpdatePath.RejectLinks(destination);
        var temporary=destination+"."+Guid.NewGuid().ToString("N")+".image.tmp";
        try
        {
            await using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {await file.WriteAsync(picture.Bytes,token);file.Flush(true);}
            if(!SHA256.HashData(await File.ReadAllBytesAsync(temporary,token)).AsSpan().SequenceEqual(SHA256.HashData(picture.Bytes)))
                throw new InvalidDataException("Metadata.VerificationFailed");
            token.ThrowIfCancellationRequested();File.Move(temporary,destination,true);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
}
