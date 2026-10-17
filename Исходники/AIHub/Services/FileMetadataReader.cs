using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

public sealed class FileMetadataReader
{
    public async Task<FileMetadataSnapshot> ReadAsync(string path, CancellationToken token)
    {
        path = Path.GetFullPath(path); var file = new FileInfo(path); var fields = new List<FileMetadataField>();
        var pictures = new List<FileMetadataPicture>(); var format = ""; var kind = "other";
        var length = file.Exists ? file.Length : 0; var ticks = file.Exists ? file.LastWriteTimeUtc.Ticks : 0;
        FileMetadataSnapshot Result(string? error = null, string? hash = null, bool cover = false, string? notice = null) =>
            new(path, length, ticks, hash, kind, format, fields, pictures, cover, notice, error);
        fields.Add(new("file:path", "File", "Metadata.Path", path));
        fields.Add(new("file:size", "File", "Metadata.Size", length.ToString()));
        if (file.Exists)
        {
            fields.Add(new("file:modified", "File", "Metadata.Modified", file.LastWriteTimeUtc.ToString("O")));
            fields.Add(new("file:attributes", "File", "Metadata.Attributes", file.Attributes.ToString()));
        }
        try
        {
            if (!file.Exists) throw new FileNotFoundException();
            Lopata.Updates.SafeUpdatePath.RejectLinks(path);
            var raw = await FileMetadataExifTool.ReadAsync(path, token);
            format = FileMetadataPolicy.RawValue(raw, "File:FileType").ToUpperInvariant();
            if(format=="EXTENDED WEBP")format="WEBP";
            var mime = FileMetadataPolicy.RawValue(raw, "File:MIMEType");
            var hasOrigin = raw.EnumerateObject().Any(p => p.Name.Contains("LOPATA", StringComparison.OrdinalIgnoreCase)
                || p.Name.EndsWith(":Software", StringComparison.OrdinalIgnoreCase) && p.Value.ToString().Contains("LOPATA", StringComparison.OrdinalIgnoreCase));
            foreach (var pair in raw.EnumerateObject())
            {
                if (pair.Name == "SourceFile") continue;
                var text = FileMetadataPolicy.Text(pair.Value);
                if (text.StartsWith("base64:", StringComparison.Ordinal))
                {
                    if (text.Length < 24 * 1024 * 1024)
                    {
                        try
                        {
                            var bytes = Convert.FromBase64String(text[7..]); FileMetadataPreview.ValidateImage(bytes);
                            using var codec = SkiaSharp.SKCodec.Create(new SkiaSharp.SKMemoryStream(bytes));
                            var imageMime = codec!.EncodedFormat == SkiaSharp.SKEncodedImageFormat.Jpeg ? "image/jpeg"
                                : codec.EncodedFormat == SkiaSharp.SKEncodedImageFormat.Png ? "image/png" : "image/unknown";
                            pictures.Add(new(pair.Name, pair.Name, imageMime, bytes));
                        }
                        catch (Exception error) when (error is FormatException or InvalidDataException) { }
                    }
                    text = "[binary: " + ((text.Length-7)*3/4) + " bytes]";
                }
                var group = FileMetadataPolicy.Origin(pair.Name) ? "Origin" : pair.Name.StartsWith("System:") ? "File"
                    : pair.Name.StartsWith("File:") || pair.Name.StartsWith("Composite:") ? "Technical" : "Other";
                fields.Add(new("raw:"+pair.Name, group, pair.Name, text, Reason: FileMetadataPolicy.Protected(pair.Name) ? "Metadata.Protected" : "Metadata.ReadOnly"));
            }
            var signed=FileMetadataPolicy.Signature(raw.EnumerateObject().Select(p=>p.Name));
            var editableSize = length <= 512L * 1024 * 1024&&!signed;
            var extension = Path.GetExtension(path).ToLowerInvariant();
            var coverEditable = false;
            if (extension is ".mp3" or ".opus" or ".flac" or ".wav")
            {
                kind = "audio";
                await MusicAudioRuntime.Default.PrepareAsync(token);
                var audio = await AudioConversionMetadata.ReadAsync(MusicAudioRuntime.Default, path, token);
                pictures.Clear();
                foreach(var pair in audio.Tags)
                    fields.Add(new("native:"+pair.Key,FileMetadataPolicy.Origin(pair.Key)?"Origin":"Other",pair.Key,pair.Value,
                        Reason:FileMetadataPolicy.Protected(pair.Key)?"Metadata.Protected":"Metadata.ReadOnly"));
                for (var i=0;i<audio.Covers.Count;i++)
                {
                    var p=audio.Covers[i]; pictures.Add(new("audio:"+i, "#"+(i+1)+" · "+p.Type+" · "+p.Description,p.Mime,p.Data));
                }
                var audioWritable=editableSize&&length<=128L*1024*1024;
                foreach(var key in FileMetadataAudio.Frames.Keys)
                    fields.Add(new("edit:"+key,"Basic","Metadata.Field."+key,audio.Tags.GetValueOrDefault(key,""),audioWritable,
                        audioWritable ? null : "Metadata.TooLarge"));
                // Validate the payload before offering a destructive operation.
                if (audioWritable) _ = await MusicLyricsTimingMetadata.AudioPayloadHashAsync(path, token);
                coverEditable = audioWritable && audio.Covers.Count <= 1;
            }
            else if (mime.StartsWith("image/",StringComparison.OrdinalIgnoreCase))
            {
                kind="image";
                var names = format == "PNG" ? FileMetadataPolicy.Png : FileMetadataPolicy.Xmp;
                foreach(var pair in names)
                {
                    var rawName=format=="PNG" ? "PNG:"+pair.Value : pair.Value;
                    var writable=editableSize && format is "PNG" or "JPEG" or "WEBP" && !(hasOrigin && pair.Key=="comment");
                    fields.Add(new("edit:"+pair.Key,"Basic","Metadata.Field."+pair.Key,FileMetadataPolicy.RawValue(raw,rawName),writable,
                        writable ? null : hasOrigin && pair.Key=="comment" ? "Metadata.Protected" : "Metadata.ReadOnly"));
                }
            }
            else if (extension==".docx" && format is "DOCX" or "ZIP")
            {
                kind="document"; var office=FileMetadataOffice.Read(path);if(office.Values.Count>0)format="DOCX";
                foreach(var pair in office.Values)
                    fields.Add(new("edit:"+pair.Key,"Basic","Metadata.Field."+pair.Key,pair.Value,office.Writable&&editableSize,"Metadata.ReadOnly"));
            }
            else if (mime.StartsWith("video/",StringComparison.OrdinalIgnoreCase)) kind="video";
            else if (format=="PDF" || mime.Contains("document",StringComparison.OrdinalIgnoreCase)) kind="document";
            var writableFile=fields.Any(f=>f.Editable)||coverEditable;
            string? hash=null;
            if(writableFile)
            {
                await using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,128*1024,FileOptions.Asynchronous|FileOptions.SequentialScan);
                hash=Convert.ToHexString(await SHA256.HashDataAsync(stream,token));
            }
            file.Refresh();
            if(file.Length!=length||file.LastWriteTimeUtc.Ticks!=ticks)throw new IOException("Metadata.FileChanged");
            return Result(hash:hash,cover:coverEditable,notice:signed?"Metadata.Signed":writableFile ? null : "Metadata.ReadOnly");
        }
        catch(OperationCanceledException){throw;}
        catch(Exception error) when(error is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or TimeoutException
            or System.Xml.XmlException or ArgumentException or FormatException or OverflowException or NotSupportedException)
        {return Result(error:error.Message.StartsWith("Metadata.",StringComparison.Ordinal)?error.Message:"Metadata.ReadFailed");}
    }
}
