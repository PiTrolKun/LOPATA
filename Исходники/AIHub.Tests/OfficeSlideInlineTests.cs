using System.IO;
using System.Text.Json;
using AIHub.Services.OfficeEditor;
using SkiaSharp;

namespace AIHub.Tests;

[TestClass]
public sealed class OfficeSlideInlineTests
{
    [TestMethod, DataRow(".pptx"), DataRow(".odp")]
    public void DraftOrderAndInlineTextSurviveProjectReadAndNativeExport(string ext)
    {
        var section = new OfficeSheetSection { Title = "Header", Body = "Body" };
        OfficeSlideCanvasModel Draft() => OfficeDraftSlideCanvas.Open(section,"Title","Body");
        using var bmp = new SKBitmap(80,40);
        OfficeSlidePicture Picture(SKColor color)
        {
            bmp.Erase(color); using var png = bmp.Encode(SKEncodedImageFormat.Png,100);
            return new(png.ToArray(),".png","image/png");
        }
        Draft().Insert!("Red",Picture(SKColors.Red));
        Draft().Insert!("Blue",Picture(SKColors.Blue));
        Draft().Objects[0].TextBlocks!()[0].SetText("Updated title");
        Draft().Objects[2].ChangeOrder!(true);
        Draft().Objects[1].ChangeOrder!(false);
        CollectionAssert.AreEqual(new[]{"Body","Title","Blue","Red"}, Draft().Objects.Select(o=>o.Name).ToArray());
        var restored = OfficeSectionContent.Read(JsonSerializer.SerializeToUtf8Bytes(new OfficeSectionDraft(1,[section])));
        var doc = OfficeNativeDocument.Open(OfficeNativeDocument.Create(ext,restored),ext);
        OfficeSlideCanvasModel Native() => doc.SlideCanvas(0)!;
        Assert.AreEqual("Updated title", Native().Objects[1].TextBlocks!()[0].Text);
        Native().Objects[1].TextBlocks!()[0].SetText("Edited on slide");
        var dir = Environment.GetEnvironmentVariable("LOPATA_INLINE_PROOF");
        if (dir != null) File.WriteAllBytes(Path.Combine(dir,"front-red"+ext),doc.Save());
        Native().Objects[^1].ChangeOrder!(false);
        var bytes = doc.Save();
        if (dir != null) File.WriteAllBytes(Path.Combine(dir,"front-blue"+ext),bytes);
        var final = OfficeNativeDocument.Open(bytes,ext);
        Assert.IsTrue(final.SlideCanvas(0)!.Objects[0].IsPicture);
        Assert.AreEqual("Edited on slide",final.SlideCanvas(0)!.Objects[2].TextBlocks!()[0].Text);
    }
}
