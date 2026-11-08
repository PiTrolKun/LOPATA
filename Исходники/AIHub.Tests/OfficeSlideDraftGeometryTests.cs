using System.IO;
using System.Text.Json;
using AIHub.Services.OfficeEditor;
using SkiaSharp;

namespace AIHub.Tests;

[TestClass]
public sealed class OfficeSlideDraftGeometryTests
{
    [TestMethod, DataRow(".pptx"), DataRow(".odp")]
    public async Task ExistingProjectDraftKeepsNewFramePositionsThroughSaveAndExport(string extension)
    {
        var token = CancellationToken.None;
        var dir = Directory.CreateTempSubdirectory("lopata-slide-draft-");
        var project = Path.Combine(dir.FullName, "Слайды" + OfficePackage.Extension);
        var work = await OfficeTextWork.OpenAsync(null, token, firstKind: OfficeSheetKind.Presentation);
        try
        {
            var shape = OfficeSheets.PrimaryId;
            var sections = OfficeSectionContent.Read("""{"Version":1,"Sections":[{"Title":"Заголовок","Body":"Текст"},{"Title":"Ещё","Body":"Продолжение"}]}"""u8.ToArray());
            var old = OfficeDraftSlideCanvas.Open(sections[0], "Title", "Body");
            old.Objects[0].SetBounds(new(820000, 650000, 4900000, 1350000));
            old.Objects[1].SetBounds(new(2580000, 2100000, 6000000, 3750000));
            await work.WriteSheetAsync(shape, JsonSerializer.SerializeToUtf8Bytes(new OfficeSectionDraft(1, sections)), token);
            await work.SaveAsync(project, token);
            work.Close(true);
            work = await OfficeTextWork.OpenAsync(project, token);
            var reopened = OfficeSectionContent.Read(await work.ReadSheetAsync(shape, token));
            Assert.AreEqual(sections[0].TitleBox, reopened[0].TitleBox);
            Assert.AreEqual(sections[0].BodyBox, reopened[0].BodyBox);
            Assert.IsNull(reopened[1].TitleBox);
            var output = Path.Combine(dir.FullName, "Экспорт" + extension);
            await work.ExportNativeSectionAsync(shape, output, token);
            var native = OfficeNativeDocument.Open(await File.ReadAllBytesAsync(output), extension);
            var slide = native.SlideCanvas(0)!;
            Assert.AreEqual(2, slide.Objects.Count);
            for (int i = 0; i < old.Objects.Count; i++)
            {
                var before = old.Objects[i].Bounds;
                var after = slide.Objects[i].Bounds;
                Assert.AreEqual(before.X / old.Width, after.X / slide.Width, 0.0001);
                Assert.AreEqual(before.Y / old.Height, after.Y / slide.Height, 0.0001);
                Assert.AreEqual(before.Width / old.Width, after.Width / slide.Width, 0.0001);
                Assert.AreEqual(before.Height / old.Height, after.Height / slide.Height, 0.0001);
            }
            Assert.AreEqual("Ещё", native.Pages[1].Blocks[0].Text);
        }
        finally { work.Close(true); dir.Delete(true); }
    }

    [TestMethod]
    public void ReplacementKeepsTheEncodedPngOrJpegAndRejectsBrokenFiles()
    {
        var path = Path.GetTempFileName();
        try
        {
            using var bitmap = new SKBitmap(16, 10);
            bitmap.Erase(SKColors.Orange);
            foreach (var format in new[] { SKEncodedImageFormat.Png, SKEncodedImageFormat.Jpeg })
            {
                using var data = bitmap.Encode(format, 90);
                var original = data.ToArray();
                File.WriteAllBytes(path, original);
                var picture = OfficeSlidePicture.Load(path);
                Assert.AreEqual(format == SKEncodedImageFormat.Png ? "image/png" : "image/jpeg", picture.MimeType);
                CollectionAssert.AreEqual(original, picture.Bytes);
                File.WriteAllBytes(path, original[..8]);
                Assert.ThrowsExactly<InvalidDataException>(() => OfficeSlidePicture.Load(path));
            }
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void BinarySnapshotsCannotBeMutatedByCallerOrSubsequentWrites()
    {
        var doc = OfficeNativeDocument.Create(".pptx", [new() { Title = "Slide" }]);
        var package = new OfficeNativePackage(doc, false);
        var bytes = new byte[] { 1, 2, 3 };
        package.AddBinary("ppt/media/probe.png", bytes);
        var snapshot = package.Snapshot();
        bytes[0] = 12;
        package.Read("ppt/media/probe.png")[1] = 21;
        package.AddBinary("ppt/media/other.png", [9]);
        package.EndRead();
        var restored = new OfficeNativePackage(snapshot(), false);
        try
        {
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, restored.Read("ppt/media/probe.png"));
            Assert.IsFalse(restored.Entries.Contains("ppt/media/other.png"));
        }
        finally { restored.EndRead(); }
    }
}
