using System.IO;
using System.IO.Compression;
using System.Xml.Linq;
using AIHub.Services;
using AIHub.Services.OfficeEditor;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;

namespace AIHub.Tests;

[TestClass]
public sealed class OfficeSlideProjectObjectTests
{
    [TestMethod]
    public async Task ProjectReopensEditedObjectsAndRetainsBothAttachedOriginals()
    {
        var corpus = Path.Combine(AppDataPaths.ProjectRoot!, "Тесты", "OfficeReader_20261107", "user-suite",
            "AI_Office_TestSuite_RU_EN", "04_presentations", "RU");
        var formats = new[] { ".odp", ".pptx" };
        if (formats.Any(ext => !File.Exists(Path.Combine(corpus, "complex_8_slides" + ext))))
            Assert.Inconclusive("Install the RU OfficeReader presentation fixtures.");
        var directory = Directory.CreateTempSubdirectory("lopata-slide-project-");
        var token = CancellationToken.None;
        var original = formats.ToDictionary(ext => ext, ext => File.ReadAllBytes(Path.Combine(corpus, "complex_8_slides" + ext)));
        var image = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAEAAAAAgCAIAAAAt/+nTAAAAT0lEQVR4nO3PUQkAIBTAwFfA/inMZgVD+HEIgwW4zVn764YLGtCCBrSgAS1oQAsa0IIGtKABLWhACxrQgga0oAEtaEALGtCCBrSgAS147AIL4EjES5VYZgAAAABJRU5ErkJggg==");
        string FileInProject(string filename) => Path.Combine(directory.FullName, filename);
        var project = FileInProject("Работа.LOPATAdoxAI");
        OfficeTextWork? work = null;
        try
        {
            foreach (var ext in formats) File.WriteAllBytes(FileInProject("source" + ext), original[ext]);
            work = await OfficeTextWork.OpenAsync(FileInProject("source.odp"), token);
            var secondary = await work.AddSheetFromFileAsync(FileInProject("source.pptx"), token);
            var ids = new Dictionary<string, string> { [".odp"] = OfficeSheets.PrimaryId, [".pptx"] = secondary.Id };
            var expected = new Dictionary<string, OfficeSlideBounds>();
            foreach (var ext in formats)
            {
                var doc = OfficeNativeDocument.Open(await work.ReadNativeSectionAsync(ids[ext], token), ext);
                var slide = doc.SlideCanvas(3)!;
                var picture = slide.Objects.Single(o => o.IsPicture);
                expected[ext] = new OfficeSlideBounds(Math.Round(slide.Width / 7), Math.Round(slide.Height / 3),
                    Math.Round(slide.Width / 4), Math.Round(slide.Height / 4));
                picture.SetBounds(expected[ext]);
                picture.ReplacePicture(new OfficeSlidePicture(image, ".png", "image/png"));
                await work.WriteNativeSectionAsync(ids[ext], doc.Save(), token);
            }
            await work.SaveAsync(project, token);
            work.Close(true);
            work = null;
            work = await OfficeTextWork.OpenAsync(project, token);
            foreach (var ext in formats)
            {
                AssertMaterial(await work.ReadNativeSectionAsync(ids[ext], token), ext, expected[ext], image);
                var export = FileInProject("exported" + ext);
                await work.ExportNativeSectionAsync(ids[ext], export, token);
                AssertMaterial(await File.ReadAllBytesAsync(export), ext, expected[ext], image);
                var exact = FileInProject("extracted" + ext);
                await work.ExportMaterialAsync(work.Sheets.Single(s => s.Id == ids[ext]).Files.Single(), exact, token);
                CollectionAssert.AreEqual(original[ext], await File.ReadAllBytesAsync(exact));
                CollectionAssert.AreEqual(original[ext], await File.ReadAllBytesAsync(FileInProject("source" + ext)));
            }
        }
        finally { work?.Close(true); directory.Delete(true); }
    }

    private static void AssertMaterial(byte[] material, string extension, OfficeSlideBounds expected, byte[] image)
    {
        var restored = OfficeNativeDocument.Open(material, extension);
        Assert.AreEqual(expected, restored.SlideCanvas(3)!.Objects.Single(o => o.IsPicture).Bounds);
        using var stream = new MemoryStream(material, false);
        if (extension == ".odp")
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            using var content = archive.GetEntry("content.xml")!.Open();
            XNamespace draw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
            XNamespace link = "http://www.w3.org/1999/xlink";
            var page = XDocument.Load(content).Descendants(draw + "page").ElementAt(3);
            var reference = page.Elements(draw + "frame").Elements(draw + "image").Single().Attribute(link + "href")!.Value;
            using var bytes = archive.GetEntry(reference)!.Open();
            AssertImage(bytes, image);
        }
        else
        {
            using var office = PresentationDocument.Open(stream, false);
            var main = office.PresentationPart!;
            var id = main.Presentation!.SlideIdList!.Elements<SlideId>().ElementAt(3).RelationshipId!.Value!;
            var slide = (SlidePart)main.GetPartById(id);
            var reference = slide.Slide!.Descendants<Picture>().Single().BlipFill!.Blip!.Embed!.Value!;
            using var bytes = slide.GetPartById(reference).GetStream(FileMode.Open, FileAccess.Read);
            AssertImage(bytes, image);
        }
    }

    private static void AssertImage(Stream bytes, byte[] expected)
    {
        using var actual = new MemoryStream();
        bytes.CopyTo(actual);
        CollectionAssert.AreEqual(expected, actual.ToArray());
    }
}
