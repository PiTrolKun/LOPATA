using System.IO;
using System.Security.Cryptography;
using AIHub.Services;
using AIHub.Services.OfficeEditor;
using AIHub.Services.OfficeReader;
using DocumentFormat.OpenXml.Packaging;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class ReaderTextPreviewNativeTests
{
    [TestMethod, TestCategory("Native")]
    public async Task EditorEnginePreservesPagesTextHeadingsNotesLinksAndCacheWithoutOffice()
    {
        RequireNative(); var root = Folder();
        var source = Path.Combine(root, "Чтение RU EN.docx");
        Fixture(source);
        var before = SHA256.HashData(await File.ReadAllBytesAsync(source));
        var previous = ComponentLicenseGate.ConfirmAsync;
        var licenses = new List<string>();
        ComponentLicenseGate.ConfirmAsync = (ids, token) => { token.ThrowIfCancellationRequested(); lock(licenses) licenses.AddRange(ids); return Task.CompletedTask; };
        using var audit = ComponentAudit.UseTestStore(Path.Combine(root, "audit"), false);
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            byte[]? first = null;
            for (var pass=0;pass<2;pass++)
            {
                await using var doc = await ReaderPreparation.OpenAsync(source,null,limit.Token);
                Assert.HasCount(2,doc.Pages);
                Assert.IsTrue(doc.Pages[1].Width > doc.Pages[1].Height,"Second section is landscape.");
                Assert.HasCount(2,doc.Headings);
                Assert.AreEqual(1,doc.Headings[1].Page);
                StringAssert.Contains((await doc.ReadTextAsync(0,limit.Token)).Text,"Первый");
                var last=await doc.ReadTextAsync(1,limit.Token);
                StringAssert.Contains(last.Text,"Последний"); Assert.IsNotEmpty(last.Runs);
                Assert.AreEqual(1,(await doc.FindAsync("Last",0,limit.Token)).Page);
                var links=await doc.ReadLinksAsync(0,limit.Token);
                Assert.IsTrue(links.Any(l=>l.Page==1),"Bookmark jump must survive the engine change.");
                Assert.IsTrue(links.Any(l=>l.Note?.Contains("Комментарий",StringComparison.Ordinal)==true));
                using(var pdf=UglyToad.PdfPig.PdfDocument.Open(doc.PdfPath!))Assert.IsGreaterThan(0,pdf.GetPage(1).NumberOfImages);
                Assert.IsTrue(File.Exists(await doc.Backend.RenderAsync(1,850,limit.Token)));
                var current=SHA256.HashData(await File.ReadAllBytesAsync(doc.PdfPath!,limit.Token));
                if(first is not null)CollectionAssert.AreEqual(first,current,"Cache must retain PDF and navigation metadata.");
                first=current;
                if(pass==0 && Environment.GetEnvironmentVariable("LOPATA_READER_EVIDENCE") is string evidence)
                {
                    File.Copy(doc.PdfPath!,Path.Combine(evidence,"editor-reader.pdf"),true);
                    File.Copy(Path.Combine(doc.Workspace,ReaderTextPreview.MetadataName),Path.Combine(evidence,"editor-reader.json"),true);
                    foreach(var page in new[]{0,1})File.Copy(await doc.Backend.RenderAsync(page,850,limit.Token),Path.Combine(evidence,$"page-{page}.png"),true);
                    File.Copy(source,Path.Combine(evidence,"fixture.docx"),true);
                }
            }
            CollectionAssert.AreEqual(before,SHA256.HashData(await File.ReadAllBytesAsync(source)));
            CollectionAssert.Contains(licenses,"office.text-surface");
            CollectionAssert.DoesNotContain(licenses,"runtime.libreoffice");
            Assert.IsFalse(OwnedProcessRegistry.Shared.GetSnapshot().Any(p=>p.Component=="Office.Reader"));
        }
        finally { ComponentLicenseGate.ConfirmAsync=previous; Directory.Delete(root,true); }
    }

    [TestMethod, TestCategory("Native")]
    public async Task TextCorruptInputAndCancellationLeaveNoOfficeWorkerAndDoNotRewriteSource()
    {
        RequireNative(); var root=Folder(); var previous=ComponentLicenseGate.ConfirmAsync;
        ComponentLicenseGate.ConfirmAsync=(ids,token)=>{token.ThrowIfCancellationRequested();CollectionAssert.DoesNotContain(ids.ToArray(),"runtime.libreoffice");return Task.CompletedTask;};
        try
        {
            var text=Path.Combine(root,"text.txt");await File.WriteAllTextAsync(text,"Текст без офиса. Read without an office.");
            await using(var doc=await ReaderPreparation.OpenAsync(text,null,CancellationToken.None,useCache:false))
                StringAssert.Contains((await doc.ReadTextAsync(0,CancellationToken.None)).Text,"офиса");
            using var canceled=new CancellationTokenSource();canceled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(()=>ReaderPreparation.OpenAsync(text,null,canceled.Token));
            var bad=Path.Combine(root,"broken.docx");await File.WriteAllTextAsync(bad,"not a package");
            await Assert.ThrowsAsync<InvalidDataException>(()=>ReaderPreparation.OpenAsync(bad,null,CancellationToken.None));
            Assert.AreEqual("not a package",await File.ReadAllTextAsync(bad));
        }
        finally {ComponentLicenseGate.ConfirmAsync=previous;Directory.Delete(root,true);}
    }
    private static void RequireNative()
    {
        if(Environment.GetEnvironmentVariable("LOPATA_READER_TEXT_NATIVE")!="1")Assert.Inconclusive("Explicit shipped WebView2 engine run required.");
    }
    private static string Folder()=>Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),"LOPATA-ReaderText-"+Guid.NewGuid().ToString("N"))).FullName;
    private static void Fixture(string path)
    {
        File.WriteAllBytes(path,OfficeTextFormats.PlainDocument("placeholder"));
        using var package=WordprocessingDocument.Open(path,true);
        var main=package.MainDocumentPart!;var body=main.Document!.Body!;body.RemoveAllChildren();
        W.Paragraph Heading(string text)=>new(new W.ParagraphProperties(new W.ParagraphStyleId{Val="Heading1"}),new W.Run(new W.Text(text)));
        body.Append(Heading("Первый First"));
        body.Append(new W.Paragraph(new W.Hyperlink(new W.Run(new W.Text("Перейти к последнему листу"))){Anchor="target"}));
        body.Append(new W.Paragraph(new W.CommentRangeStart{Id="0"},new W.Run(new W.Text("Проверка комментария")),new W.CommentRangeEnd{Id="0"},new W.Run(new W.CommentReference{Id="0"})));
        var image=main.AddImagePart(ImagePartType.Png);
        using(var bitmap=new SkiaSharp.SKBitmap(32,32))
        {
            bitmap.Erase(SkiaSharp.SKColors.Red);
            using var picture=SkiaSharp.SKImage.FromBitmap(bitmap);
            using var encoded=picture.Encode(SkiaSharp.SKEncodedImageFormat.Png,100);
            using var stream=image.GetStream();encoded.SaveTo(stream);
        }
        var drawing=new W.Drawing {InnerXml=$"""
            <wp:inline xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture"><wp:extent cx="914400" cy="914400"/><wp:docPr id="1" name="Red square"/><a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture"><pic:pic><pic:nvPicPr><pic:cNvPr id="0" name="Red square"/><pic:cNvPicPr/></pic:nvPicPr><pic:blipFill><a:blip r:embed="{main.GetIdOfPart(image)}"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill><pic:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="914400" cy="914400"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></pic:spPr></pic:pic></a:graphicData></a:graphic></wp:inline>
            """};
        body.Append(new W.Paragraph(new W.Run(drawing)));
        body.Append(new W.Paragraph(new W.ParagraphProperties(new W.SectionProperties(new W.PageSize{Width=11906,Height=16838}))));
        var last=Heading("Последний Last");last.InsertAt(new W.BookmarkStart{Id="1",Name="target"},1);last.Append(new W.BookmarkEnd{Id="1"});body.Append(last);
        body.Append(new W.Paragraph(new W.Run(new W.Text("Текст второй страницы. Second page text."))));
        body.Append(new W.SectionProperties(new W.PageSize{Width=16838,Height=11906,Orient=W.PageOrientationValues.Landscape}));
        main.AddNewPart<WordprocessingCommentsPart>().Comments=new W.Comments(new W.Comment(new W.Paragraph(new W.Run(new W.Text("Комментарий Comment")))){Id="0",Author="Test"});
    }
}
