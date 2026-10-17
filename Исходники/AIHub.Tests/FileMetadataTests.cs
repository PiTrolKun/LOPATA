using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using AIHub.Models;
using AIHub.Services;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using SkiaSharp;

namespace AIHub.Tests;

[TestClass,DoNotParallelize]
public sealed class FileMetadataTests
{
    [TestMethod,DataRow("PNG"),DataRow("JPEG"),DataRow("WEBP")]
    public async Task ImagesPreservePixelsAndUnknownFieldsWhileEditingUnicodeText(string format)
    {
        var root=MusicOutputTests.Temporary();
        try
        {
            var path=Path.Combine(root,"Картинка 日本語."+(format=="JPEG"?"jpg":format.ToLowerInvariant()));
            File.WriteAllBytes(path,Picture(SKColors.Orange,format));
            if(format=="PNG")PngMetadataWriter.Write(path,new Dictionary<string,string>{{"Unfamiliar","opaque 123"},{"LOPATA","{\"seed\":18}"},{"parameters","original prompt"}});
            else await FileMetadataExifTool.WriteAsync(path,new Dictionary<string,string>{{"XMP-photoshop:Instructions","unfamiliar original"}},default);
            var reader=new FileMetadataReader();var before=await reader.ReadAsync(path,default);
            Record(format+"-before",before);
            if(Environment.GetEnvironmentVariable("LOPATA_METADATA_EVIDENCE") is {Length:>0} evidence && format!="PNG")
            {
                var copy=Path.Combine(evidence,"probe-"+format+Path.GetExtension(path));File.Copy(path,copy,true);
                await FileMetadataExifTool.WriteAsync(copy,new Dictionary<string,string>{{"XMP-dc:Title","Новая 🎵 обложка 日本語"},{"XMP-dc:Creator","Автор\n-config\n$(never execute)"}},default);
                Record(format+"-direct-write",await reader.ReadAsync(copy,default));
            }
            Assert.IsNull(before.Error,before.Error);Assert.IsNotNull(before.Hash);Assert.IsTrue(before.Fields.Count>10);
            var hash=await FileMetadataIntegrity.ContentHashAsync(path,"image",format,default);
            const string title="Новая 🎵 обложка 日本語";const string author="Автор\n-config\n$(never execute)";
            var result=await new FileMetadataEditor().SaveAsync(before,new(new Dictionary<string,string>{{"title",title},{"author",author}}),default);
            Assert.IsTrue(result.Saved,result.Error);
            var after=await reader.ReadAsync(path,default);
            Assert.AreEqual(title,after.Fields.Single(f=>f.Id=="edit:title").Value);Assert.AreEqual(author,after.Fields.Single(f=>f.Id=="edit:author").Value);
            Assert.AreEqual(hash,await FileMetadataIntegrity.ContentHashAsync(path,"image",format,default));
            if(format=="PNG")Assert.AreEqual("opaque 123",PngTextMetadata.Read(path)["Unfamiliar"]);
            else Assert.IsTrue(after.Fields.Any(f=>f.Value=="unfamiliar original"));
            var clear=await new FileMetadataEditor().SaveAsync(after,new(new Dictionary<string,string>{{"title",""}}),default);
            Assert.IsTrue(clear.Saved,clear.Error);Assert.AreEqual("",(await reader.ReadAsync(path,default)).Fields.Single(f=>f.Id=="edit:title").Value);
        }
        finally{Directory.Delete(root,true);}
    }
    [TestMethod,DataRow(MusicAudioFormat.Mp3),DataRow(MusicAudioFormat.Opus),DataRow(MusicAudioFormat.Flac),DataRow(MusicAudioFormat.Wav)]
    public async Task AudioEditsAndDeclaredCoverKeepOriginalGenerationAndAudio(MusicAudioFormat format)
    {
        var root=MusicOutputTests.Temporary();
        try
        {
            var pcm=Path.Combine(root,"source.wav");MusicOutputTests.WriteWave(pcm);var path=Path.Combine(root,"Песня."+MusicOutputSettings.Extension(format));
            var tags=new Dictionary<string,string>{{"title","Old title"},{"artist","Original artist"},{"LOPATA_MODEL","Original music model"},
                {"LOPATA_LYRICS","Immutable source lyrics"},{"LOPATA_COVER_MODEL","Z-Image Turbo"},{"LOPATA_COVER_SOURCE","standard"},{"Unfamiliar","opaque value"}};
            await MusicAudioEncoder.Default.EncodeAsync(pcm,path,format,192,tags,default);
            var reader=new FileMetadataReader();var before=await reader.ReadAsync(path,default);Assert.IsNull(before.Error,before.Error);Assert.HasCount(1,before.Pictures);Assert.IsTrue(before.CanReplaceCover);
            var hash=await MusicLyricsTimingMetadata.AudioPayloadHashAsync(path,default);
            var cover=new FileMetadataCover(Picture(SKColors.Green,"PNG"),"image/png",null,"Нарисовано вручную");
            var result=await new FileMetadataEditor().SaveAsync(before,new(new Dictionary<string,string>{{"title","Новая песня"},{"comment","Комментарий\nв две строки"}},cover),default);
            Assert.IsTrue(result.Saved,result.Error);Assert.AreEqual(hash,await MusicLyricsTimingMetadata.AudioPayloadHashAsync(path,default));
            var after=await AudioConversionMetadata.ReadAsync(MusicAudioRuntime.Default,path,default);
            Assert.AreEqual("Новая песня",after.Tags["title"]);Assert.AreEqual("opaque value",after.Tags["Unfamiliar"]);
            Assert.AreEqual(tags["LOPATA_MODEL"],after.Tags["LOPATA_MODEL"]);Assert.AreEqual(tags["LOPATA_LYRICS"],after.Tags["LOPATA_LYRICS"]);
            Assert.AreEqual(cover.SourceName,after.Tags["LOPATA_COVER_MODEL"]);Assert.AreEqual("user-declared",after.Tags["LOPATA_COVER_SOURCE"]);
            Assert.IsTrue(after.Tags["LOPATA_COVER_ORIGINAL"].Contains("Z-Image Turbo"));CollectionAssert.AreEqual(cover.Bytes,after.Covers.Single().Data);
            if(format==MusicAudioFormat.Mp3)
            {
                var again=await reader.ReadAsync(path,default);var alternate=new FileMetadataCover(Picture(SKColors.Blue,"JPEG"),"image/jpeg","krea",null);
                var second=await new FileMetadataEditor().SaveAsync(again,new(new Dictionary<string,string>(),alternate),default);Assert.IsTrue(second.Saved,second.Error);
                var metadata=await AudioConversionMetadata.ReadAsync(MusicAudioRuntime.Default,path,default);
                Assert.AreEqual(ImageGenerationCatalog.Get("krea").Name,metadata.Tags["LOPATA_COVER_MODEL"]);
                Assert.AreEqual(after.Tags["LOPATA_COVER_ORIGINAL"],metadata.Tags["LOPATA_COVER_ORIGINAL"]);
                Assert.AreEqual(tags["LOPATA_MODEL"],metadata.Tags["LOPATA_MODEL"]);
            }
        }
        finally{Directory.Delete(root,true);}
    }
    [TestMethod]
    public async Task OfficeEditingKeepsEveryOtherZipMemberAndRejectsSignedPackages()
    {
        var root=MusicOutputTests.Temporary();
        try
        {
            var path=Path.Combine(root,"Документ.docx");
            using(var doc=WordprocessingDocument.Create(path,DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
            {doc.AddMainDocumentPart().Document=new Document(new Body(new Paragraph(new Run(new Text("Неизменный текст")))));doc.PackageProperties.Title="Original";doc.PackageProperties.Creator="Writer";}
            Dictionary<string,string> Members(){using var zip=ZipFile.OpenRead(path);var core=FileMetadataOffice.CoreName(zip);return zip.Entries.Where(e=>e.FullName!=core).ToDictionary(e=>e.FullName,e=>{using var stream=e.Open();return Convert.ToHexString(SHA256.HashData(stream));});}
            var old=Members();var reader=new FileMetadataReader();var snapshot=await reader.ReadAsync(path,default);Assert.IsNull(snapshot.Error,snapshot.Error);
            var result=await new FileMetadataEditor().SaveAsync(snapshot,new(new Dictionary<string,string>{{"title","Новый заголовок"},{"comment","Описание"}}),default);
            Assert.IsTrue(result.Saved,result.Error);CollectionAssert.AreEquivalent(old.ToArray(),Members().ToArray());
            using(var zip=ZipFile.Open(path,ZipArchiveMode.Update)){var entry=zip.CreateEntry("_xmlsignatures/sig1.xml");using var writer=new StreamWriter(entry.Open());writer.Write("signature");}
            var signed=await reader.ReadAsync(path,default);Assert.IsFalse(signed.Fields.Any(f=>f.Editable));
            var bytes=File.ReadAllBytes(path);var blocked=await new FileMetadataEditor().SaveAsync(signed,new(new Dictionary<string,string>{{"title","Forbidden"}}),default);
            Assert.IsFalse(blocked.Saved);CollectionAssert.AreEqual(bytes,File.ReadAllBytes(path));
        }
        finally{Directory.Delete(root,true);}
    }
    [TestMethod]
    public async Task ForgedProtectedPatchStaleSnapshotAndCancellationNeverAlterSource()
    {
        var root=MusicOutputTests.Temporary();
        try
        {
            var path=Path.Combine(root,"file.png");File.WriteAllBytes(path,Picture(SKColors.Red,"PNG"));PngMetadataWriter.Write(path,new Dictionary<string,string>{{"LOPATA","original creation"}});
            var reader=new FileMetadataReader();var snapshot=await reader.ReadAsync(path,default);var bytes=File.ReadAllBytes(path);
            var forged=snapshot with{Fields=snapshot.Fields.Append(new("edit:LOPATA_MODEL","Basic","Model","",true)).ToArray()};
            var denied=await new FileMetadataEditor().SaveAsync(forged,new(new Dictionary<string,string>{{"LOPATA_MODEL","Fake"}}),default);
            Assert.IsFalse(denied.Saved);CollectionAssert.AreEqual(bytes,File.ReadAllBytes(path));
            PngMetadataWriter.Write(path,new Dictionary<string,string>{{"ExternalEdit","changed elsewhere"}});bytes=File.ReadAllBytes(path);
            var stale=await new FileMetadataEditor().SaveAsync(snapshot,new(new Dictionary<string,string>{{"title","Changed"}}),default);
            Assert.AreEqual("Metadata.FileChanged",stale.Error);CollectionAssert.AreEqual(bytes,File.ReadAllBytes(path));
            using var cancel=new CancellationTokenSource();cancel.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(()=>new FileMetadataEditor().SaveAsync(snapshot,new(new Dictionary<string,string>{{"title","Changed"}}),cancel.Token));
            CollectionAssert.AreEqual(bytes,File.ReadAllBytes(path));Assert.HasCount(1,Directory.GetFiles(root));
        }
        finally{Directory.Delete(root,true);}
    }
    [TestMethod]
    public async Task ImageExtractionIsExactAndCannotOverwriteAnOpenSource()
    {
        var root=MusicOutputTests.Temporary();
        try
        {
            var source=Path.Combine(root,"source.png");var bytes=Picture(SKColors.Blue,"PNG");File.WriteAllBytes(source,bytes);
            var picture=new FileMetadataPicture("test","Cover","image/png",bytes);var destination=Path.Combine(root,"cover.png");
            await FileMetadataImages.ExportAsync(picture,destination,[source],default);CollectionAssert.AreEqual(bytes,File.ReadAllBytes(destination));
            await Assert.ThrowsAsync<IOException>(()=>FileMetadataImages.ExportAsync(picture,source,[source],default));CollectionAssert.AreEqual(bytes,File.ReadAllBytes(source));
        }
        finally{Directory.Delete(root,true);}
    }
    [TestMethod,DataRow(1,"webm"),DataRow(2,"webm"),DataRow(3,"webm"),DataRow(3,"mp4")]
    public async Task VideoPreviewUsesThirdFrameAndShortVideosUseFirst(int frames,string format)
    {
        var root=MusicOutputTests.Temporary();
        try
        {
            var raw=Path.Combine(root,"frames.bgra");using(var stream=File.Create(raw))
                foreach(var color in new[]{SKColors.Red,SKColors.Green,SKColors.Blue}.Take(frames))for(var i=0;i<16*16;i++)stream.Write([color.Blue,color.Green,color.Red,255]);
            var video=Path.Combine(root,"test."+format);
            var args=new List<string>{"-nostdin","-v","error","-f","rawvideo","-pixel_format","bgra","-video_size","16x16","-framerate","1","-i",raw,"-frames:v",frames.ToString(),"-c:v",format=="webm"?"libvpx-vp9":"libopenh264","-pix_fmt","yuv420p"};
            if(format=="webm")args.AddRange(["-lossless","1"]);args.Add(video);
            await FileMetadataProcess.RunAsync(Path.Combine(AppContext.BaseDirectory,"CaptureRuntime","ffmpeg.exe"),
                args,default);
            var snapshot=await new FileMetadataReader().ReadAsync(video,default);Assert.IsNull(snapshot.Error,snapshot.Error);Assert.AreEqual("video",snapshot.Kind);
            var preview=await FileMetadataPreview.ReadAsync(snapshot,default);Assert.IsNotNull(preview);using var bitmap=SKBitmap.Decode(preview);var pixel=bitmap.GetPixel(bitmap.Width/2,bitmap.Height/2);
            if(frames==3)Assert.IsTrue(pixel.Blue>pixel.Red+100);else Assert.IsTrue(pixel.Red>pixel.Blue+100);
        }
        finally{Directory.Delete(root,true);}
    }
    internal static byte[] Picture(SKColor color,string format)
    {
        using var surface=SKSurface.Create(new SKImageInfo(64,48));surface.Canvas.Clear(color);using var image=surface.Snapshot();
        using var data=image.Encode(format switch{"PNG"=>SKEncodedImageFormat.Png,"JPEG"=>SKEncodedImageFormat.Jpeg,_=>SKEncodedImageFormat.Webp},90);return data.ToArray();
    }
    private static void Record(string name,object value)
    {
        if(Environment.GetEnvironmentVariable("LOPATA_METADATA_EVIDENCE") is not {Length:>0} directory)return;
        Directory.CreateDirectory(directory);File.WriteAllText(Path.Combine(directory,name+".json"),System.Text.Json.JsonSerializer.Serialize(value));
    }
}
