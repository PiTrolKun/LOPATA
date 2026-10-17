using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.IO.Compression;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using AIHub.Models;
using AIHub.Services;
using SkiaSharp;

namespace AIHub.Tests;

[TestClass,DoNotParallelize]
public sealed class FileMetadataPreservationTests
{
    [TestMethod]
    public async Task TwoAudioPicturesSurviveTagEditingAndReplacementIsRejected()
    {
        var root=MusicOutputTests.Temporary();
        try
        {
            var source=Path.Combine(root,"source.wav");MusicOutputTests.WriteWave(source);var file=Path.Combine(root,"album.flac");
            await MusicAudioEncoder.Default.EncodeAsync(source,file,MusicAudioFormat.Flac,192,new Dictionary<string,string>{{"title","Original"},{"LOPATA_MODEL","Original model"}},default);
            var back=FileMetadataTests.Picture(SKColors.Purple,"PNG");await AudioMetadataWriter.AttachAsync(file,MusicAudioFormat.Flac,new Dictionary<string,string>(),[new("image/png",4,"back",back)],default);
            var reader=new FileMetadataReader();var original=await reader.ReadAsync(file,default);Assert.IsNull(original.Error,original.Error);Assert.HasCount(2,original.Pictures);Assert.IsFalse(original.CanReplaceCover);
            var changed=await new FileMetadataEditor().SaveAsync(original,new(new Dictionary<string,string>{{"title","New title"}}),default);Assert.IsTrue(changed.Saved,changed.Error);
            var after=await reader.ReadAsync(file,default);Assert.HasCount(2,after.Pictures);for(var i=0;i<2;i++)CollectionAssert.AreEqual(original.Pictures[i].Bytes,after.Pictures[i].Bytes);
            var hash=SHA256.HashData(File.ReadAllBytes(file));var rejected=await new FileMetadataEditor().SaveAsync(after,new(new Dictionary<string,string>(),new(back,"image/png","z-image",null)),default);
            Assert.IsFalse(rejected.Saved);Assert.AreEqual("Metadata.CoverReadOnly",rejected.Error);CollectionAssert.AreEqual(hash,SHA256.HashData(File.ReadAllBytes(file)));
        }
        finally{Directory.Delete(root,true);}
    }
    [TestMethod]
    public async Task EmbeddedExifPictureIsReadExportedAndRetainedDuringJpegEdits()
    {
        var root=MusicOutputTests.Temporary();
        try
        {
            var file=Path.Combine(root,"photo.jpg");var thumbnail=Path.Combine(root,"thumbnail.jpg");File.WriteAllBytes(file,FileMetadataTests.Picture(SKColors.Goldenrod,"JPEG"));
            var bytes=FileMetadataTests.Picture(SKColors.Blue,"JPEG");File.WriteAllBytes(thumbnail,bytes);await FileMetadataExifTool.PrepareAsync(default);
            await FileMetadataProcess.RunAsync(Path.Combine(FileMetadataExifTool.DirectoryPath,"exiftool.exe"),["-config","","-overwrite_original","-IFD1:ThumbnailImage<="+thumbnail,file],default);
            var reader=new FileMetadataReader();var before=await reader.ReadAsync(file,default);Assert.IsNull(before.Error,before.Error);Assert.HasCount(1,before.Pictures);CollectionAssert.AreEqual(bytes,before.Pictures[0].Bytes);
            var result=await new FileMetadataEditor().SaveAsync(before,new(new Dictionary<string,string>{{"title","A photo"}}),default);Assert.IsTrue(result.Saved,result.Error);
            var after=await reader.ReadAsync(file,default);CollectionAssert.AreEqual(bytes,after.Pictures[0].Bytes);
            var output=Path.Combine(root,"export.jpg");await FileMetadataImages.ExportAsync(after.Pictures[0],output,[file],default);CollectionAssert.AreEqual(bytes,File.ReadAllBytes(output));
        }
        finally{Directory.Delete(root,true);}
    }
    [TestMethod]
    public async Task PdfAndLockedFilesRemainUnchangedAndMissingDataIsNotClaimed()
    {
        var root=MusicOutputTests.Temporary();
        try
        {
            var pdf=Path.Combine(root,"document.pdf");WritePdf(pdf);var reader=new FileMetadataReader();var snapshot=await reader.ReadAsync(pdf,default);
            Assert.IsNull(snapshot.Error,snapshot.Error);Assert.AreEqual("document",snapshot.Kind);Assert.IsTrue(snapshot.Fields.Any(f=>f.Value=="Read-only document"));Assert.IsNull(snapshot.Hash);
            var bytes=File.ReadAllBytes(pdf);var blocked=await new FileMetadataEditor().SaveAsync(snapshot,new(new Dictionary<string,string>{{"title","New title"}}),default);Assert.IsFalse(blocked.Saved);CollectionAssert.AreEqual(bytes,File.ReadAllBytes(pdf));
            var path=Path.Combine(root,"image.png");File.WriteAllBytes(path,FileMetadataTests.Picture(SKColors.Red,"PNG"));var image=await reader.ReadAsync(path,default);
            await using(var locked=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
            {
                var unavailable=await reader.ReadAsync(path,default);Assert.IsNotNull(unavailable.Error);
                var result=await new FileMetadataEditor().SaveAsync(image,new(new Dictionary<string,string>{{"title","New title"}}),default);Assert.IsFalse(result.Saved);
            }
            Assert.AreEqual(image.Hash,Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        }
        finally{Directory.Delete(root,true);}
    }
    private static void WritePdf(string path)
    {
        using var output=new MemoryStream();void Write(string value)=>output.Write(Encoding.ASCII.GetBytes(value));
        Write("%PDF-1.4\n");var offsets=new List<long>{0};var objects=new[]{"<< /Type /Catalog /Pages 2 0 R >>","<< /Type /Pages /Kids [3 0 R] /Count 1 >>","<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>","<< /Title (Read-only document) /Author (Writer) >>"};
        for(var i=0;i<objects.Length;i++){offsets.Add(output.Position);Write($"{i+1} 0 obj\n{objects[i]}\nendobj\n");}
        var xref=output.Position;Write($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");foreach(var offset in offsets.Skip(1))Write($"{offset:0000000000} 00000 n \n");
        Write($"trailer\n<< /Size {offsets.Count} /Root 1 0 R /Info 4 0 R >>\nstartxref\n{xref}\n%%EOF\n");File.WriteAllBytes(path,output.ToArray());
    }
    [TestMethod]
    public async Task SignatureRelationshipsAndMalformedXmlNeverEnableOfficeWriting()
    {
        Assert.IsTrue(FileMetadataPolicy.Signature(["JUMBF:Claim"]));Assert.IsTrue(FileMetadataPolicy.Signature(["XMP-c2pa:Manifest"]));
        var root=MusicOutputTests.Temporary();
        try
        {
            var path=Path.Combine(root,"signed.docx");
            using(var document=WordprocessingDocument.Create(path,DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
            {document.AddMainDocumentPart().Document=new Document(new Body(new Paragraph(new Run(new Text("Body")))));document.PackageProperties.Title="Signed title";}
            using(var zip=ZipFile.Open(path,ZipArchiveMode.Update))
            {
                var entry=zip.GetEntry("_rels/.rels")!;XDocument xml;using(var stream=entry.Open())xml=XDocument.Load(stream);
                xml.Root!.Add(new XElement(xml.Root.Name.Namespace+"Relationship",new XAttribute("Type","http://schemas.openxmlformats.org/package/2006/relationships/digital-signature/origin"),new XAttribute("Id","signature"),new XAttribute("Target","private/custom-signature.xml")));
                entry.Delete();using var output=zip.CreateEntry("_rels/.rels").Open();xml.Save(output);
            }
            var reader=new FileMetadataReader();var signed=await reader.ReadAsync(path,default);Assert.IsFalse(signed.Fields.Any(f=>f.Editable));
            var bytes=File.ReadAllBytes(path);var denied=await new FileMetadataEditor().SaveAsync(signed,new(new Dictionary<string,string>{{"title","Forbidden"}}),default);Assert.IsFalse(denied.Saved);CollectionAssert.AreEqual(bytes,File.ReadAllBytes(path));
            var bad=Path.Combine(root,"malformed.docx");File.Copy(path,bad);
            using(var zip=ZipFile.Open(bad,ZipArchiveMode.Update)){zip.GetEntry("_rels/.rels")!.Delete();using var stream=new StreamWriter(zip.CreateEntry("_rels/.rels").Open());stream.Write("<bad");}
            var broken=await reader.ReadAsync(bad,default);Assert.IsNotNull(broken.Error);
            var good=Path.Combine(root,"good.png");File.WriteAllBytes(good,FileMetadataTests.Picture(SKColors.Green,"PNG"));Assert.IsNull((await reader.ReadAsync(good,default)).Error);
        }
        finally{Directory.Delete(root,true);}
    }
}
