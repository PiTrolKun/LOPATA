using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

public sealed class FileMetadataEditor(FileMetadataReader? reader = null)
{
    private readonly FileMetadataReader _reader=reader??new();
    public async Task<FileMetadataSaveResult> SaveAsync(FileMetadataSnapshot snapshot,FileMetadataPatch patch,CancellationToken token)
    {
        var directory=Path.GetDirectoryName(snapshot.Path)!;
        var temporary=Path.Combine(directory,".lopata-metadata-"+Guid.NewGuid().ToString("N")+Path.GetExtension(snapshot.Path));
        var backup=temporary+".rollback";var committed=false;
        try
        {
            FileMetadataPolicy.Validate(snapshot,patch);
            if(patch.Values.Count==0&&patch.Cover is null)return new(snapshot.Path,false);
            Lopata.Updates.SafeUpdatePath.RejectLinks(snapshot.Path);
            var fresh=await _reader.ReadAsync(snapshot.Path,token);
            if(fresh.Error is not null||fresh.Hash!=snapshot.Hash||fresh.Length!=snapshot.Length||fresh.ModifiedTicks!=snapshot.ModifiedTicks)
                throw new IOException("Metadata.FileChanged");
            FileMetadataPolicy.Validate(fresh,patch);
            await using(var source=new FileStream(snapshot.Path,FileMode.Open,FileAccess.Read,FileShare.Read,128*1024,FileOptions.Asynchronous))
            {
                if(Convert.ToHexString(await SHA256.HashDataAsync(source,token))!=snapshot.Hash)throw new IOException("Metadata.FileChanged");
                source.Position=0;
                await using var destination=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,128*1024,FileOptions.Asynchronous);
                await source.CopyToAsync(destination,token);destination.Flush(true);
            }
            var payload=await FileMetadataIntegrity.ContentHashAsync(temporary,snapshot.Kind,snapshot.Format,token);
            var provenance=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            if(patch.Cover is { } cover)
            {
                var origin=snapshot.Fields.Where(f=>f.Id.StartsWith("native:LOPATA_COVER_",StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(f=>f.Id[7..],f=>f.Value,StringComparer.OrdinalIgnoreCase);
                if(!origin.ContainsKey("LOPATA_COVER_ORIGINAL")&&origin.Count>0)
                    provenance["LOPATA_COVER_ORIGINAL"]=JsonSerializer.Serialize(origin);
                provenance[MusicCoverProvenance.ModelTag]=cover.SourceName;
                provenance[MusicCoverProvenance.SourceTag]="user-declared";
            }
            switch(snapshot.Kind)
            {
                case "audio":await FileMetadataAudio.WriteAsync(snapshot.Path,temporary,patch,provenance,token);break;
                case "image" when snapshot.Format=="PNG":
                    PngMetadataWriter.Write(temporary,patch.Values.ToDictionary(p=>FileMetadataPolicy.Png[p.Key],p=>p.Value),token);break;
                case "image":await FileMetadataExifTool.WriteAsync(temporary,patch.Values.ToDictionary(p=>FileMetadataPolicy.Xmp[p.Key],p=>p.Value),token);break;
                case "document" when snapshot.Format=="DOCX":await FileMetadataOffice.WriteAsync(temporary,patch.Values,token);break;
                default:throw new InvalidDataException("Metadata.ReadOnly");
            }
            if(payload!=await FileMetadataIntegrity.ContentHashAsync(temporary,snapshot.Kind,snapshot.Format,token))
                throw new InvalidDataException("Metadata.ContentChanged");
            var result=await _reader.ReadAsync(temporary,token);
            if(result.Error is not null)throw new InvalidDataException("Metadata.VerificationFailed");
            foreach(var pair in patch.Values)
                if(!result.Fields.Any(f=>f.Id=="edit:"+pair.Key&&f.Value==pair.Value))throw new InvalidDataException("Metadata.VerificationFailed");
            foreach(var field in snapshot.Fields.Where(f=>f.Group=="Origin"))
            {
                var normalized=string.Concat(field.Id.Where(char.IsLetterOrDigit));
                if(patch.Cover is not null&&(normalized.EndsWith("LOPATACOVERMODEL",StringComparison.OrdinalIgnoreCase)
                    ||normalized.EndsWith("LOPATACOVERSOURCE",StringComparison.OrdinalIgnoreCase)))continue;
                if(!result.Fields.Any(f=>f.Id==field.Id&&f.Value==field.Value))throw new InvalidDataException("Metadata.Protected");
            }
            if(patch.Cover is null)
            {
                if(snapshot.Pictures.Count!=result.Pictures.Count||snapshot.Pictures.Where((p,i)=>!p.Bytes.AsSpan().SequenceEqual(result.Pictures[i].Bytes)).Any())
                    throw new InvalidDataException("Metadata.VerificationFailed");
            }
            else if(!result.Pictures.Any(p=>p.Bytes.AsSpan().SequenceEqual(patch.Cover.Bytes)))throw new InvalidDataException("Metadata.VerificationFailed");
            await using(var original=new FileStream(snapshot.Path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete,128*1024,FileOptions.Asynchronous))
            {
                if(Convert.ToHexString(await SHA256.HashDataAsync(original,token))!=snapshot.Hash)throw new IOException("Metadata.FileChanged");
                Lopata.Updates.SafeUpdatePath.RejectLinks(snapshot.Path);token.ThrowIfCancellationRequested();
                File.Replace(temporary,snapshot.Path,backup);committed=true;
            }
            return new(snapshot.Path,true);
        }
        catch(OperationCanceledException){throw;}
        catch(Exception error) when(error is InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException or JsonException or TimeoutException
            or System.Xml.XmlException or ArgumentException or FormatException or OverflowException or NotSupportedException)
        {return new(snapshot.Path,false,error.Message.StartsWith("Metadata.",StringComparison.Ordinal)?error.Message:"Metadata.SaveFailed");}
        finally
        {
            TryRemove(temporary);
            // Keep an intact recovery copy if replacement reported failure after creating one.
            if(committed)TryRemove(backup);
        }
    }
    private static void TryRemove(string path)
    {try{if(File.Exists(path))File.Delete(path);}catch(IOException){}catch(UnauthorizedAccessException){}}
}
