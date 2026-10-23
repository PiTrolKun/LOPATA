using System.IO;
using System.Text;
using AIHub.Services;
using AIHub.Services.VectorGraphics;
using SkiaSharp;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VectorRasterExportTests
{
    [TestMethod, DataRow("png"), DataRow("jpeg"), DataRow("webp")]
    public async Task NativeExportRendersOriginalAtExactResolutionWithRecordedProvenance(string format)
    {
        using var fixture = new VectorSettingsSessionTests.Fixture(); var item = fixture.AddLegacy();
        var before = File.ReadAllBytes(VectorStore.FilePath(item.Request.Id, "image.svg"));
        Directory.CreateDirectory(fixture.SessionRoot);
        var gate = ComponentLicenseGate.ConfirmAsync; ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
        using var audit = ComponentAudit.UseTestStore(Path.Combine(fixture.SessionRoot, "audit"), false);
        try
        {
            var path = Path.Combine(fixture.SessionRoot, "Экспорт 日本語." + (format == "jpeg" ? "jpg" : format));
            await VectorRasterExport.SaveAsync(item, path, new(2000, 1000, format), default);
            using var image = SKBitmap.Decode(path);
            Assert.AreEqual(2000, image.Width); Assert.AreEqual(1000, image.Height);
            Assert.IsTrue(image.GetPixel(995, 500).Green > 200, "Original 1-unit line must be rerendered sharply at the export scale.");
            if (format == "jpeg") Assert.IsTrue(image.GetPixel(10, 10).Red > 240);
            else Assert.AreEqual((byte)0, image.GetPixel(10, 10).Alpha);
            var facts = await FileMetadataExifTool.ReadAsync(path, default);
            StringAssert.Contains(facts.ToString(), "LOPATA"); StringAssert.Contains(facts.ToString(), "recorded-model");
            StringAssert.Contains(facts.ToString(), "vector-graphics/raster-export");
            Assert.IsTrue(facts.EnumerateObject().Any(p => p.Name.EndsWith(":ImageWidth", StringComparison.Ordinal) && p.Value.ToString() == "2000"));
            CollectionAssert.AreEqual(before, File.ReadAllBytes(VectorStore.FilePath(item.Request.Id, "image.svg")));
            Assert.IsFalse(File.Exists(VectorStore.FilePath(item.Request.Id, "preview.png")), "Export must work without a raster preview.");
        }
        finally { ComponentLicenseGate.ConfirmAsync = gate; }
    }

    [TestMethod]
    public async Task InvalidDimensionsCancellationAndInternalDestinationsDoNotOverwriteFiles()
    {
        Assert.Throws<InvalidDataException>(() => new VectorRasterOptions(0, 1).Validate());
        Assert.Throws<InvalidDataException>(() => new VectorRasterOptions(16384, 1, "webp").Validate());
        Assert.Throws<InvalidDataException>(() => new VectorRasterOptions(65501, 1, "jpeg").Validate());
        Assert.IsTrue(VectorRasterExport.EstimatedBytes(new(70000, 1, "png")) > 0, "PNG is not limited to the image-generator presets.");
        Assert.Throws<InvalidDataException>(() => VectorRasterExport.EstimatedBytes(new(int.MaxValue, int.MaxValue)));
        using var fixture = new VectorSettingsSessionTests.Fixture(); var item = fixture.AddLegacy();
        Directory.CreateDirectory(fixture.SessionRoot); var path = Path.Combine(fixture.SessionRoot, "keep.png");
        await File.WriteAllTextAsync(path, "preserve"); using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => VectorRasterExport.SaveAsync(item, path, new(32, 32), canceled.Token));
        Assert.AreEqual("preserve", await File.ReadAllTextAsync(path));
        var source = VectorStore.FilePath(item.Request.Id, "image.svg"); var before = await File.ReadAllBytesAsync(source);
        await Assert.ThrowsAsync<InvalidDataException>(() => VectorRasterExport.SaveAsync(item, source, new(32, 32), default));
        await Assert.ThrowsAsync<InvalidDataException>(() => VectorRasterExport.SaveAsync(item, VectorStore.FilePath(item.Request.Id, "preview.png"), new(32, 32), default));
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(source));
    }
}
