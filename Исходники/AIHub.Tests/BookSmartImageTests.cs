using System.IO;
using System.Security.Cryptography;
using System.Text;
using AIHub.Services;
using AIHub.Services.Books.Smart;
using SkiaSharp;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class BookSmartImageTests
{
    [TestMethod, DataRow("webp"), DataRow("jpg"), DataRow("png")]
    public async Task RasterImagesBecomePngAtOriginalPixelSizeWithoutChangingSourceBytes(string extension)
    {
        using var bitmap = new SKBitmap(37, 23); bitmap.Erase(SKColors.CornflowerBlue);
        using var encoded = bitmap.Encode(extension switch { "webp" => SKEncodedImageFormat.Webp, "jpg" => SKEncodedImageFormat.Jpeg, _ => SKEncodedImageFormat.Png }, 95);
        var source = encoded.ToArray(); var hash = SHA256.HashData(source);
        var png = await BookSmartImage.NormalizeAsync(source, extension, CancellationToken.None);
        CollectionAssert.AreEqual(hash, SHA256.HashData(source)); Assert.AreNotSame(source, png);
        CollectionAssert.AreEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png.Take(8).ToArray());
        using var result = SKBitmap.Decode(png); Assert.IsNotNull(result); Assert.AreEqual(37, result.Width); Assert.AreEqual(23, result.Height);
    }

    [TestMethod]
    public async Task PngNormalizationPreservesPixelsAndTransparency()
    {
        using var bitmap = new SKBitmap(11, 7); bitmap.Erase(SKColors.Transparent); bitmap.SetPixel(3, 4, SKColors.Red);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var normalized = SKBitmap.Decode(await BookSmartImage.NormalizeAsync(encoded.ToArray(), ".PNG", CancellationToken.None));
        Assert.AreEqual((byte)0, normalized.GetPixel(0, 0).Alpha); Assert.AreEqual(SKColors.Red, normalized.GetPixel(3, 4));
    }

    [TestMethod]
    public async Task InvalidRasterAndCancelledOperationDoNotBecomeSuccessfulImages()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => BookSmartImage.NormalizeAsync([1, 2, 3], ".jpg", CancellationToken.None));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => BookSmartImage.NormalizeAsync([1, 2, 3], ".png", cancellation.Token));
    }

    [TestMethod, DataRow("<image href='https://example.com/image.png'/>") , DataRow("<image href='relative.png'/>") , DataRow("<script>alert(1)</script>")]
    public async Task ExternalSvgResourcesAreRejectedBeforeAnyRendererIsStarted(string content)
    {
        var bytes = Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' width='37' height='23'>" + content + "</svg>");
        await Assert.ThrowsAsync<InvalidDataException>(() => BookSmartImage.NormalizeAsync(bytes, ".svg", CancellationToken.None));
    }

    [TestMethod]
    public async Task SvgRootAttributesCannotBypassTheExternalResourceValidation()
    {
        var bytes = Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' width='37' height='23' style='fill:url(https://example.com/image.svg)'><rect width='37' height='23'/></svg>");
        await Assert.ThrowsAsync<InvalidDataException>(() => BookSmartImage.NormalizeAsync(bytes, ".svg", CancellationToken.None));
    }

    [TestMethod, TestCategory("Native")]
    public async Task ShippedCalibreQtSvgRasterizerPreservesDefaultSizeWithoutChangingSvg()
    {
        if (Environment.GetEnvironmentVariable("LOPATA_BOOK_SMART_IMAGE_NATIVE") != "1")
            Assert.Inconclusive("Explicit bundled-calibre SVG native test opt-in is required.");
        var bytes = Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' width='37' height='23' viewBox='0 0 37 23'><rect width='37' height='23' fill='#123456'/></svg>");
        var hash = SHA256.HashData(bytes); var previous = ComponentLicenseGate.ConfirmAsync;
        ComponentLicenseGate.ConfirmAsync = (_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; };
        try
        {
            var png = await BookSmartImage.NormalizeAsync(bytes, ".svg", CancellationToken.None);
            CollectionAssert.AreEqual(hash, SHA256.HashData(bytes));
            using var image = SKBitmap.Decode(png); Assert.IsNotNull(image);
            Assert.AreEqual(37, image.Width); Assert.AreEqual(23, image.Height); Assert.AreEqual(new SKColor(0x12, 0x34, 0x56), image.GetPixel(10, 10));
        }
        finally { ComponentLicenseGate.ConfirmAsync = previous; }
    }
}
