using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace AIHub.Services;

internal static class FileMetadataIntegrity
{
    internal static async Task<string> ContentHashAsync(string path,string kind,string format,CancellationToken token)
    {
        if(kind=="audio")return await MusicLyricsTimingMetadata.AudioPayloadHashAsync(path,token);
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if(format=="DOCX")
        {
            using var zip=ZipFile.OpenRead(path);
            var core=FileMetadataOffice.CoreName(zip);
            foreach(var entry in zip.Entries.Where(e=>e.FullName!=core).OrderBy(e=>e.FullName,StringComparer.Ordinal))
            {
                hash.AppendData(Encoding.UTF8.GetBytes(entry.FullName+"\0"));await using var stream=entry.Open();
                hash.AppendData(await SHA256.HashDataAsync(stream,token));
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        await using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.Asynchronous);
        if(format=="PNG")
        {
            var header=new byte[8];await input.ReadExactlyAsync(header,token);hash.AppendData(header);
            while(input.Position<input.Length)
            {
                await input.ReadExactlyAsync(header,token);var length=BinaryPrimitives.ReadUInt32BigEndian(header);
                var type=Encoding.ASCII.GetString(header,4,4);var keep=type is not ("iTXt" or "tEXt" or "zTXt");
                if(keep)hash.AppendData(header);await CopyAsync(input,(long)length+4,keep?hash:null,token);
            }
        }
        else if(format=="WEBP")
        {
            input.Position=12;var header=new byte[8];
            while(input.Position<input.Length)
            {
                await input.ReadExactlyAsync(header,token);var length=BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
                var type=Encoding.ASCII.GetString(header,0,4);
                if(type=="VP8X")
                {
                    if(length!=10)throw new InvalidDataException("Metadata.ReadFailed");
                    var data=new byte[10];await input.ReadExactlyAsync(data,token);data[0]&=0xf3;hash.AppendData(header);hash.AppendData(data);
                }
                else
                {
                    var keep=type is not ("XMP " or "EXIF");if(keep)hash.AppendData(header);
                    await CopyAsync(input,(long)length+(length&1),keep?hash:null,token);
                }
            }
        }
        else if(format=="JPEG")
        {
            var head=new byte[2];await input.ReadExactlyAsync(head,token);
            if(!head.AsSpan().SequenceEqual(new byte[]{255,216}))throw new InvalidDataException("Metadata.ReadFailed");
            hash.AppendData(head);
            while(input.Position<input.Length)
            {
                await input.ReadExactlyAsync(head,token);if(head[0]!=255)throw new InvalidDataException("Metadata.ReadFailed");
                while(head[1]==255){var b=input.ReadByte();if(b<0)throw new EndOfStreamException();head[1]=(byte)b;}
                var marker=head[1];
                if(marker==218){hash.AppendData(head);await CopyAsync(input,input.Length-input.Position,hash,token);break;}
                if(marker==217){hash.AppendData(head);break;}
                var lengthBytes=new byte[2];await input.ReadExactlyAsync(lengthBytes,token);var length=BinaryPrimitives.ReadUInt16BigEndian(lengthBytes)-2;
                var keep=marker is not (225 or 237 or 254);if(keep){hash.AppendData(head);hash.AppendData(lengthBytes);}
                await CopyAsync(input,length,keep?hash:null,token);
            }
        }
        else throw new InvalidDataException("Metadata.ReadOnly");
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static async Task CopyAsync(Stream stream,long count,IncrementalHash? hash,CancellationToken token)
    {
        if(count<0||count>stream.Length-stream.Position)throw new InvalidDataException("Metadata.ReadFailed");
        var buffer=new byte[65536];while(count>0)
        {var n=(int)Math.Min(count,buffer.Length);await stream.ReadExactlyAsync(buffer.AsMemory(0,n),token);hash?.AppendData(buffer,0,n);count-=n;}
    }
}
