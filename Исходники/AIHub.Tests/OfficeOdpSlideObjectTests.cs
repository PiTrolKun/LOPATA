using System.IO;
using System.IO.Compression;
using System.Xml.Linq;
using AIHub.Services;
using AIHub.Services.OfficeEditor;

namespace AIHub.Tests;

[TestClass]
public class OfficeOdpSlideObjectTests
{
    private static readonly XNamespace Draw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    private static readonly XNamespace Svg = "urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0";
    private static readonly XNamespace Text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private static readonly XNamespace Pres = "urn:oasis:names:tc:opendocument:xmlns:presentation:1.0";
    private static readonly XNamespace Office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private static readonly XNamespace Style = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
    private static readonly XNamespace Link = "http://www.w3.org/1999/xlink";
    private static readonly XNamespace Manifest = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0";
    private static readonly byte[] Pixel = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRy8AAAAASUVORK5CYII=");

    private static OfficeNativePackage Draft()
    {
        var bytes = OfficeNativeDocument.Create(".odp", [new() { Title = "First slide", Body = "Body" }, new() { Title = "Second slide" }]);
        var pkg = new OfficeNativePackage(bytes, false);
        pkg.EndRead();
        return pkg;
    }

    private static XElement Picture(string name, string href) =>
        new(Draw + "frame", new XAttribute(Draw + "name", name), new XAttribute(Svg + "x", "1in"),
            new XAttribute(Svg + "y", "12.7mm"), new XAttribute(Svg + "width", "96px"), new XAttribute(Svg + "height", "3pc"),
            new XElement(Draw + "image", new XAttribute(Link + "href", href), new XAttribute(Draw + "mime-type", "image/png")));

    [TestMethod]
    public void ReplacementOnlyChangesSelectedFrameAndUpdatesManifest()
    {
        var pkg = Draft();
        const string original = "Pictures/shared.png";
        pkg.AddBinary(original, Pixel);
        var manifest = pkg.Xml("META-INF/manifest.xml").Root!;
        manifest.Add(new XElement(Manifest + "file-entry", new XAttribute(Manifest + "full-path", original),
            new XAttribute(Manifest + "media-type", "image/png")));
        var pages = pkg.Xml("content.xml").Descendants(Draw + "page").ToArray();
        var first = Picture("My photo", original);
        first.Add(new XElement(first.Element(Draw + "image")!));
        first.Add(new XElement(Svg + "desc", "Cool skyline"));
        pages[0].Add(first);
        pages[1].Add(Picture("Sibling", original));
        XElement savedSibling;
        using (var beforeZip = new ZipArchive(new MemoryStream(pkg.Snapshot()())))
        using (var content = beforeZip.GetEntry("content.xml")!.Open())
            savedSibling = XDocument.Load(content).Descendants(Draw + "page").ElementAt(1);
        var canvas = OfficeSlideFormat.ReadOdpObjects(pkg, 0)!;
        Assert.AreEqual(960, canvas.Width, 0.1);
        Assert.AreEqual(540, canvas.Height, 0.1);
        var photo = canvas.Objects.Single(o => o.IsPicture);
        var before = photo.Bounds;
        Assert.AreEqual(new OfficeSlideBounds(72, 36, 72, 36), before);
        photo.SetBounds(new OfficeSlideBounds(150, 25, 110, 72));
        photo.ReplacePicture(new OfficeSlidePicture(Pixel, ".png", "image/png"));
        var body = canvas.Objects.Single(o => o.Name == "Body");
        body.SetBounds(body.Bounds with { X = 11.25, Height = 22.75 });
        body.SetBounds(body.Bounds with { Y = 18 });
        using var zip = new ZipArchive(new MemoryStream(pkg.Snapshot()()));
        XDocument Xml(string part) { using var stream = zip.GetEntry(part)!.Open(); return XDocument.Load(stream); }
        var after = Xml("content.xml").Descendants(Draw + "page").ToArray();
        var target = after[0].Elements(Draw + "frame").Single(e => (string?)e.Attribute(Draw + "name") == "My photo");
        Assert.AreEqual("150pt", (string?)target.Attribute(Svg + "x"));
        Assert.AreEqual("72pt", (string?)target.Attribute(Svg + "height"));
        var imagePath = (string)target.Elements(Draw + "image").Single().Attribute(Link + "href")!;
        Assert.AreNotEqual(original, imagePath);
        using var pixels = zip.GetEntry(imagePath)!.Open(); using var stream = new MemoryStream(); pixels.CopyTo(stream);
        CollectionAssert.AreEqual(Pixel, stream.ToArray());
        Assert.IsNotNull(zip.GetEntry(original));
        Assert.AreEqual("Cool skyline", target.Element(Svg + "desc")!.Value);
        Assert.AreEqual("image/png", (string?)Xml("META-INF/manifest.xml").Descendants(Manifest + "file-entry")
            .Single(e => (string?)e.Attribute(Manifest + "full-path") == imagePath).Attribute(Manifest + "media-type"));
        Assert.IsTrue(XNode.DeepEquals(savedSibling, after[1]));
        Assert.AreEqual("11.25pt", (string?)after[0].Elements(Draw + "frame").Single(e => (string?)e.Attribute(Draw + "name") == "Body").Attribute(Svg + "x"));
    }

    [TestMethod]
    public void EnumeratesByZOrderAndSkipsTransformsLockedAndObjectPreviews()
    {
        var pkg = Draft();
        var content = pkg.Xml("content.xml");
        var page = content.Descendants(Draw + "page").First();
        var title = page.Elements(Draw + "frame").First();
        title.SetAttributeValue(Pres + "class", "title");
        title.SetAttributeValue(Draw + "z-index", "20");
        page.Add(new XElement(Draw + "g", new XAttribute(Draw + "transform", "rotate(0.5)"), Picture("Rotated", "x")));
        var chart = Picture("Chart fallback", "fallback.png");
        chart.AddFirst(new XElement(Draw + "object", new XAttribute(Link + "href", "Object 1/")));
        page.Add(chart);
        var inherited = Picture("Locked", "lock.png");
        inherited.SetAttributeValue(Draw + "style-name", "child");
        content.Descendants(Office + "automatic-styles").First().Add(
            new XElement(Style + "style", new XAttribute(Style + "family", "graphic"), new XAttribute(Style + "name", "parent"),
                new XElement(Style + "graphic-properties", new XAttribute(Style + "protect", "position size"))),
            new XElement(Style + "style", new XAttribute(Style + "family", "graphic"), new XAttribute(Style + "name", "child"),
                new XAttribute(Style + "parent-style-name", "parent")));
        page.Add(inherited);
        var linked = new XElement(Draw + "a", new XAttribute(Link + "href", "https://example.com/"), Picture("Linked image", "pic.png"));
        page.Add(linked);
        var nested = Picture("Excessive nesting", "deep.png");
        for (var i = 0; i < 130; i++) nested = new XElement(Draw + "g", nested);
        page.Add(nested);
        var canvas = OfficeSlideFormat.ReadOdpObjects(pkg, 0)!;
        CollectionAssert.AreEqual(new[] { "Body", "Linked image", "Title" }, canvas.Objects.Select(o => o.Name).ToArray());
        var selected = canvas.Objects.Last();
        selected.SetBounds(selected.Bounds with { X = 37 });
        Assert.AreEqual("true", (string?)title.Attribute(Pres + "user-transformed"));
        Assert.AreEqual("https://example.com/", (string?)linked.Attribute(Link + "href"));
    }

    [TestMethod, DataRow("RU"), DataRow("EN")]
    public void SuppliedPresentationEditsTextAndPictureGeometryInItsOriginalFormat(string language)
    {
        var path = Path.Combine(AppDataPaths.ProjectRoot!, "Тесты", "OfficeReader_20261107", "user-suite", "AI_Office_TestSuite_RU_EN",
            "04_presentations", language, "complex_8_slides.odp");
        if (!File.Exists(path)) Assert.Inconclusive("Install the OfficeReader sample corpus to run this test.");
        var source = File.ReadAllBytes(path);
        var doc = OfficeNativeDocument.Open(source, ".odp");
        var slide = doc.SlideCanvas(3)!;
        var title = slide.Objects.Single(o => o.Name == "TextBox 2");
        var photo = slide.Objects.Single(o => o.IsPicture);
        var titleBounds = new OfficeSlideBounds(91, 62, 520, 48);
        var pictureBounds = new OfficeSlideBounds(180, 180, 240, 120);
        var bitmap = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAEAAAAAgCAIAAAAt/+nTAAAAT0lEQVR4nO3PUQkAIBTAwFfA/inMZgVD+HEIgwW4zVn764YLGtCCBrSgAS1oQAsa0IIGtKABLWhACxrQgga0oAEtaEALGtCCBrSgAS147AIL4EjES5VYZgAAAABJRU5ErkJggg==");
        title.SetBounds(titleBounds);
        photo.SetBounds(pictureBounds);
        photo.ReplacePicture(new(bitmap, ".png", "image/png"));
        var result = doc.Save();
        var reopened = OfficeNativeDocument.Open(result, ".odp");
        var restored = reopened.SlideCanvas(3)!;
        Assert.AreEqual(titleBounds, restored.Objects.Single(o => o.Name == title.Name).Bounds);
        Assert.AreEqual(pictureBounds, restored.Objects.Single(o => o.IsPicture).Bounds);
        Assert.IsFalse(reopened.SlideCanvas(4)!.Objects.Any(o => o.IsPicture), "Table preview must remain bound to its table.");
        Assert.IsFalse(reopened.SlideCanvas(5)!.Objects.Any(o => o.IsPicture), "Chart preview must remain bound to its chart.");
        using var original = new ZipArchive(new MemoryStream(source));
        using var edited = new ZipArchive(new MemoryStream(result));
        foreach (var entry in original.Entries.Where(e => e.FullName is not ("content.xml" or "META-INF/manifest.xml")))
        {
            using var before = entry.Open();
            using var after = edited.GetEntry(entry.FullName)!.Open();
            CollectionAssert.AreEqual(System.Security.Cryptography.SHA256.HashData(before), System.Security.Cryptography.SHA256.HashData(after), entry.FullName);
        }
        if (Environment.GetEnvironmentVariable("LOPATA_SLIDE_OBJECT_EVIDENCE") is { Length: > 0 } evidence)
        {
            Directory.CreateDirectory(evidence);
            File.WriteAllBytes(Path.Combine(evidence, language + "-geometry.odp"), result);
        }
    }
}
