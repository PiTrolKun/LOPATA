using System.IO;
using System.IO.Compression;
using System.Xml.Linq;
using AIHub.Services;
using AIHub.Services.OfficeEditor;
using DocumentFormat.OpenXml.Packaging;

namespace AIHub.Tests;

[TestClass]
public sealed class OfficeSlidePptxObjectTests
{
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";

    [TestMethod, DataRow("RU"), DataRow("EN")]
    public void MoveResizeAndReplaceOnlySelectedPictureOnTheFourthSlide(string language)
    {
        var path = Path.Combine(AppDataPaths.ProjectRoot!, "Тесты", "OfficeReader_20261107", "user-suite", "AI_Office_TestSuite_RU_EN",
            "04_presentations", language, "complex_8_slides.pptx");
        if (!File.Exists(path)) Assert.Inconclusive("Install the private slide fixtures for this test.");
        var original = File.ReadAllBytes(path);
        var package = new OfficeNativePackage(original, false);
        var model = OfficeSlideFormat.ReadPptxObjects(package, 3)!;
        var picture = model.Objects.Single(o => o.IsPicture);
        var caption = model.Objects.Single(o => o.Name == "TextBox 5");
        var b = picture.Bounds;
        var resized = b with { X = b.X + 600000, Y = b.Y - 200000, Width = b.Width * 0.5, Height = Math.Round(b.Height * 0.5) };
        var moved = caption.Bounds with { X = caption.Bounds.X + 300000, Y = caption.Bounds.Y + 250000 };
        picture.SetBounds(resized);
        caption.SetBounds(moved);
        var replacement = RedPixel();
        picture.ReplacePicture(replacement);
        int entries = package.Entries.Count;
        picture.ReplacePicture(replacement);
        Assert.AreEqual(entries, package.Entries.Count);
        var output = package.Snapshot()();
        package.EndRead();
        using var stream = new MemoryStream(output);
        using var document = PresentationDocument.Open(stream, false);
        var slideList = document.PresentationPart!.Presentation!.SlideIdList!;
        var id = (DocumentFormat.OpenXml.Presentation.SlideId)slideList.ChildElements[3];
        var slide = (SlidePart)document.PresentationPart.GetPartById(id.RelationshipId!.Value!);
        var pic = slide.Slide!.Descendants<DocumentFormat.OpenXml.Presentation.Picture>().Single();
        var xf = pic.ShapeProperties!.Transform2D!;
        Assert.AreEqual((long)resized.X, xf.Offset!.X!.Value);
        Assert.AreEqual((long)resized.Y, xf.Offset.Y!.Value);
        Assert.AreEqual((long)resized.Width, xf.Extents!.Cx!.Value);
        Assert.AreEqual((long)resized.Height, xf.Extents.Cy!.Value);
        var media = (ImagePart)slide.GetPartById(pic.BlipFill!.Blip!.Embed!.Value!);
        Assert.AreEqual("image/png", media.ContentType);
        using var bitmap = media.GetStream();
        using var savedPixels = new MemoryStream();
        bitmap.CopyTo(savedPixels);
        CollectionAssert.AreEqual(replacement.Bytes, savedPixels.ToArray());
        var reopened = new OfficeNativePackage(output, false);
        var reloaded = OfficeSlideFormat.ReadPptxObjects(reopened, 3)!;
        Assert.AreEqual(moved, reloaded.Objects.Single(o => o.Name == "TextBox 5").Bounds);
        reopened.EndRead();
        using var before = new ZipArchive(new MemoryStream(original));
        using var after = new ZipArchive(new MemoryStream(output));
        foreach (var part in before.Entries.Where(e => e.FullName is not ("ppt/slides/slide4.xml" or "ppt/slides/_rels/slide4.xml.rels" or "[Content_Types].xml")))
        {
            using var old = part.Open();
            using var current = after.GetEntry(part.FullName)!.Open();
            CollectionAssert.AreEqual(System.Security.Cryptography.SHA256.HashData(old), System.Security.Cryptography.SHA256.HashData(current), part.FullName);
        }
        if (Environment.GetEnvironmentVariable("LOPATA_PPTX_OBJECT_EVIDENCE") is { Length: > 0 } evidence)
        {
            Directory.CreateDirectory(evidence);
            File.WriteAllBytes(Path.Combine(evidence, language + "-layout.pptx"), output);
        }
    }

    [TestMethod]
    public void ReorderedSlideEditsInheritedMasterPositionWithoutChangingLayoutOrSiblings()
    {
        var source = OfficeSlideFormat.Create(".pptx", [new() {Title="First"}, new() {Title="Second"}]);
        var package = new OfficeNativePackage(source, false);
        var list = package.Xml("ppt/presentation.xml").Root!.Element(P + "sldIdLst")!;
        var second = list.Elements().Last(); second.Remove(); list.AddFirst(second);
        const string layoutPath = "ppt/slideLayouts/slideLayout1.xml", masterPath = "ppt/slideMasters/slideMaster1.xml";
        var slideTitle = package.Xml("ppt/slides/slide2.xml").Descendants(P + "sp").First();
        var layoutShape = new XElement(slideTitle);
        var masterShape = new XElement(slideTitle);
        static void Placeholder(XElement shape, string type, int index)
        {
            shape.Descendants(P + "nvPr").Single().Add(new XElement(P+"ph", new XAttribute("idx",index),new XAttribute("type",type)));
        }
        Placeholder(slideTitle, "obj", 7); Placeholder(layoutShape, "ctrTitle", 7); Placeholder(masterShape,"title",99);
        slideTitle.Element(P+"spPr")!.RemoveNodes(); layoutShape.Element(P+"spPr")!.RemoveNodes();
        package.Xml(layoutPath).Descendants(P + "spTree").Single().Add(layoutShape);
        package.Xml(masterPath).Descendants(P + "spTree").Single().Add(masterShape);
        var before = package.Snapshot()(); package.EndRead();
        package = new OfficeNativePackage(before, false);
        var shape = OfficeSlideFormat.ReadPptxObjects(package,0)!.Objects.First();
        Assert.AreEqual(new OfficeSlideBounds(640000,360000,10912000,1040000),shape.Bounds);
        var target = shape.Bounds with { X = 156000, Height = 611111 };
        shape.SetBounds(target);
        Assert.AreEqual(target,OfficeSlideFormat.ReadPptxObjects(package,0)!.Objects.First().Bounds);
        var saved = package.Snapshot()(); package.EndRead();
        using var old = new ZipArchive(new MemoryStream(before));
        using var current = new ZipArchive(new MemoryStream(saved));
        foreach (var part in old.Entries.Where(p => p.FullName != "ppt/slides/slide2.xml"))
        {
            using var input = part.Open(); using var output = current.GetEntry(part.FullName)!.Open();
            CollectionAssert.AreEqual(System.Security.Cryptography.SHA256.HashData(input),System.Security.Cryptography.SHA256.HashData(output));
        }
    }

    [TestMethod]
    public void GroupTransformsUseSlideSpaceAndProtectedObjectsAreSkipped()
    {
        var package = new OfficeNativePackage(OfficeSlideFormat.Create(".pptx",[new() {Title="Grouped"}]),false);
        var tree = package.Xml("ppt/slides/slide1.xml").Descendants(P+"spTree").Single();
        var title = tree.Elements(P+"sp").First();
        title.Remove();
        var group = new XElement(P+"grpSp",new XElement(P+"nvGrpSpPr",new XElement(P+"cNvPr", new XAttribute("id",4)),
            new XElement(P+"cNvGrpSpPr"),new XElement(P+"nvPr")),new XElement(P+"grpSpPr",new XElement(A+"xfrm",
                new XElement(A+"off",new XAttribute("x",1000),new XAttribute("y",2000)),
                new XElement(A+"ext",new XAttribute("cx",200000),new XAttribute("cy",300000)),
                new XElement(A+"chOff",new XAttribute("x",100),new XAttribute("y",200)),
                new XElement(A+"chExt",new XAttribute("cx",100000),new XAttribute("cy",100000)))),title);
        tree.Add(group);
        var body = tree.Elements(P+"sp").Single();
        body.Descendants(P+"cNvSpPr").Single().Add(new XElement(A+"spLocks",new XAttribute("noMove",1)));
        var shape = OfficeSlideFormat.ReadPptxObjects(package,0)!.Objects.Single();
        Assert.AreEqual(1000 + (640000-100)*2,shape.Bounds.X);
        Assert.AreEqual(2000 + (360000-200)*3,shape.Bounds.Y);
        shape.SetBounds(new(1200,2300,40000,60000));
        Assert.AreEqual("200",(string?)title.Descendants(A+"off").Single().Attribute("x"));
        Assert.AreEqual("300",(string?)title.Descendants(A+"off").Single().Attribute("y"));
        Assert.AreEqual("20000",(string?)title.Descendants(A+"ext").Single().Attribute("cx"));
        group.Element(P+"grpSpPr")!.Element(A+"xfrm")!.SetAttributeValue("rot",60000);
        Assert.IsEmpty(OfficeSlideFormat.ReadPptxObjects(package,0)!.Objects);
        package.EndRead();
    }

    [TestMethod]
    public void SharedRasterAndSvgOverrideAreReplacedOnlyOnTheChosenPicture()
    {
        var package = new OfficeNativePackage(OfficeSlideFormat.Create(".pptx",[new(){Title="Pictures"}]),false);
        var slide = package.Xml("ppt/slides/slide1.xml");
        var tree = slide.Descendants(P+"spTree").Single();
        var rels = package.Xml("ppt/slides/_rels/slide1.xml.rels").Root!;
        var image = RedPixel(); package.AddBinary("ppt/media/shared.png",image.Bytes);
        rels.Add(new XElement(Rel+"Relationship",new XAttribute("Id","Shared"),new XAttribute("Type",R.NamespaceName+"/image"),
            new XAttribute("Target","../media/shared.png")));
        XElement Picture(int number) => new(P+"pic",new XElement(P+"nvPicPr",
            new XElement(P+"cNvPr",new XAttribute("id",number),new XAttribute("name","Pic "+number)),
            new XElement(P+"cNvPicPr"),new XElement(P+"nvPr")),new XElement(P+"blipFill",new XElement(A+"blip",
            new XAttribute(R+"embed","Shared"),new XElement(A+"extLst",new XElement(A+"ext",new XAttribute("uri","legacy"),
                new XElement(XName.Get("svgBlip","http://schemas.microsoft.com/office/drawing/2016/SVG/main"),new XAttribute(R+"embed","Shared")))))),
            new XElement(tree.Elements(P+"sp").First().Element(P+"spPr")!));
        tree.Add(Picture(4),Picture(5));
        var picture = OfficeSlideFormat.ReadPptxObjects(package,0)!.Objects.First(o=>o.IsPicture);
        picture.ReplacePicture(image);
        var blips=slide.Descendants(A+"blip").ToArray();
        Assert.AreNotEqual("Shared",(string?)blips[0].Attribute(R+"embed"));
        Assert.IsFalse(blips[0].Descendants().Any(e=>e.Name.LocalName=="svgBlip"));
        Assert.AreEqual("Shared",(string?)blips[1].Attribute(R+"embed"));
        Assert.IsTrue(blips[1].Descendants().Any(e=>e.Name.LocalName=="svgBlip"));
        package.EndRead();
    }

    private static OfficeSlidePicture RedPixel() => new(Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4z8AAAAMBAQDJ/pLvAAAAAElFTkSuQmCC"),".png","image/png");
}
