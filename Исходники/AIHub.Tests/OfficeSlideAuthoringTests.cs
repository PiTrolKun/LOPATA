using System.IO;
using AIHub.Services.OfficeEditor;
using SkiaSharp;

namespace AIHub.Tests;

[TestClass]
public class OfficeSlideAuthoringTests
{
    [TestMethod]
    public void UtilitiesShowTheEditorAndRetainExternalAndPanelRoutes()
    {
        var ids = AIHub.Services.ScenarioNavigationCatalog.ScenarioCards("utilities_text").Select(n => n.Id).ToArray();
        CollectionAssert.Contains(ids, AIHub.Services.ScenarioNavigationCatalog.OfficeEditor);
        CollectionAssert.DoesNotContain(ids, AIHub.Services.ScenarioNavigationCatalog.OfficeReader);
        CollectionAssert.DoesNotContain(ids, AIHub.Services.ScenarioNavigationCatalog.Dictation);
        Assert.IsTrue(AIHub.Services.ScenarioNavigationCatalog.Children("utilities_text").Count() >= ids.Length + 2);
    }
    [TestMethod, DataRow(".pptx"), DataRow(".odp")]
    public void AddRotateReopenAndDelete(string ext)
    {
        using var bitmap = new SKBitmap(120, 40);
        bitmap.Erase(SKColors.Red);
        using (var canvas = new SKCanvas(bitmap))
        using (var ink = new SKPaint { Color = SKColors.Blue }) canvas.DrawRect(60, 0, 60, 40, ink);
        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var picture = new OfficeSlidePicture(png.ToArray(), ".png", "image/png");
        var draft = new OfficeSheetSection { Title = "Original", Body = "Body" };
        var doc = OfficeNativeDocument.Open(OfficeNativeDocument.Create(ext, [draft]), ext);
        OfficeSlideCanvasModel Page() => doc.SlideCanvas(0)!;
        Page().Insert!("Inserted label", null);
        Page().Insert!("Picture", picture);
        var before = Page().Objects[^1].Bounds;
        Page().Objects[^1].Rotate!(90);
        var rotated = OfficeNativeDocument.Open(doc.Save(), ext);
        var selected = rotated.SlideCanvas(0)!.Objects[^1];
        Assert.AreEqual(90d, selected.Angle!(), .0001);
        Assert.AreEqual(before.Width, selected.Bounds.Width, .0001);
        Assert.AreEqual(before.X + before.Width/2, selected.Bounds.X + selected.Bounds.Width/2, .0001);
        Assert.IsTrue(rotated.Pages[0].Blocks.Any(b => b.Text == "Inserted label"));
        if (Environment.GetEnvironmentVariable("LOPATA_AUTHORING_PROOF") is {} dir)
            File.WriteAllBytes(Path.Combine(dir, "rotated" + ext), rotated.Save());
        selected.Rotate!(33.5);
        var angled = OfficeNativeDocument.Open(rotated.Save(), ext).SlideCanvas(0)!.Objects[^1];
        Assert.AreEqual(123.5, angled.Angle!(), .0001);
        Assert.AreEqual(before.X, angled.Bounds.X, .0001);
        selected.Delete!();
        rotated.SlideCanvas(0)!.Objects[^1].Delete!();
        var final = OfficeNativeDocument.Open(rotated.Save(), ext);
        Assert.AreEqual(2, final.SlideCanvas(0)!.Objects.Count);
        Assert.IsFalse(final.Pages[0].Blocks.Any(b => b.Text == "Inserted label"));

        var project = OfficeDraftSlideCanvas.Open(draft, "title", "body");
        project.Objects[0].Delete!();
        project.Insert!("Draft label", null);
        project.Insert!("Picture", picture);
        OfficeDraftSlideCanvas.Open(draft, "title", "body").Objects[^1].Rotate!(-90);
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new OfficeSectionDraft(1, [draft]));
        var exported = OfficeNativeDocument.Open(OfficeNativeDocument.Create(ext, OfficeSectionContent.Read(bytes)), ext);
        var objects = exported.SlideCanvas(0)!.Objects;
        Assert.AreEqual(3, objects.Count);
        Assert.AreEqual(270d, objects[^1].Angle!(), .0001);
        Assert.IsTrue(exported.Pages[0].Blocks.Any(b => b.Text == "Draft label"));
        Assert.IsFalse(exported.Pages[0].Blocks.Any(b => b.Text == "Original"));
    }
}
