using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media.Imaging;
using AIHub.Models;
using AIHub.Services;
using SkiaSharp;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class ImageOutputScalingTests
{
    private string _root = null!;
    [TestInitialize] public void Initialize() => _root = Path.Combine(Path.GetTempPath(), "lopata-output-" + Guid.NewGuid().ToString("N"));
    [TestCleanup] public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private ImageGenerationRequest Request(int width = 256, int height = 256, int longest = 1280) =>
        new(Guid.NewGuid().ToString("N"), "krea", "  Стена <каменная> & кот 🐈\nExact prompt.  ", width, height,
            [42], "unused", ImageGenerationSessionStore.Create(_root))
        { Metadata = new("Автор 🐈"), SubmittedAt = DateTimeOffset.Now, OutputFolder = Path.Combine(_root, "saved"), OutputLongestSide = longest };

    [TestMethod]
    public void OutputDimensionsPreserveRatiosWithoutModelLimitsOrMultipleOf64()
    {
        foreach (var source in new[] { (256, 256), (1536, 2048), (2048, 1536), (2048, 1152), (1152, 2048), (320, 256) })
        foreach (var longest in ImageOutputDimensions.Presets)
        {
            var size = ImageOutputDimensions.Fit(source.Item1, source.Item2, longest);
            Assert.AreEqual(longest == 0 ? Math.Max(source.Item1, source.Item2) : longest, Math.Max(size.Width, size.Height));
            var scale = (double)size.Width / source.Item1;
            Assert.IsTrue(Math.Abs(size.Height - source.Item2 * scale) <= 2);
        }
        Assert.AreEqual((2880, 3840), ImageOutputDimensions.Fit(1536, 2048, 3840));
        Assert.AreEqual((1920, 1080), ImageOutputDimensions.Fit(2048, 1152, 1920));
        Assert.AreEqual(0, ImageOutputDimensions.Normalize(7680));
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageOutputDimensions.Fit(256, 256, 7680));
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageOutputDimensions.Fit(0, 256, 1280));
    }

    [TestMethod]
    [DataRow(256, 256, 1280, 1280, 1280)]
    [DataRow(256, 320, 1920, 1536, 1920)]
    [DataRow(320, 256, 3840, 3840, 3072)]
    [DataRow(1280, 960, 1280, 1280, 960)]
    [DataRow(2048, 1152, 1280, 1280, 720)]
    public async Task RuntimePublishesResizedPngWithOriginalMetadataAndIdempotentDelivery(int width, int height, int longest, int expectedWidth, int expectedHeight)
    {
        var request = Request(width, height, longest); var worker = new Worker();
        var runtime = new ImageGenerationRuntime(worker);
        var turn = await runtime.RunAsync(request, [], null, CancellationToken.None);
        var result = turn.Results.Single(); Assert.IsNull(result.ProcessingError); Assert.IsTrue(result.Exported); Assert.IsFalse(result.Reviewed);
        var original = ImageGenerationSessionStore.ResultPath(request, 0); var ready = ImageGenerationOutput.PathFor(request, 0);
        Assert.AreNotEqual(original, ready); Assert.IsTrue(ImageGenerationRuntime.IsValidImage(original, request));
        using var input = File.OpenRead(ready);
        var frame = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames.Single();
        Assert.AreEqual(expectedWidth, frame.PixelWidth); Assert.AreEqual(expectedHeight, frame.PixelHeight);
        var metadata = (BitmapMetadata)frame.Metadata;
        Assert.AreEqual("Автор 🐈", metadata.Author.Single()); Assert.AreEqual(request.Prompt.Trim(), metadata.Comment);
        Assert.AreEqual("LOPATA", metadata.ApplicationName);
        var originalFields = PngTextMetadata.Read(original); var fields = PngTextMetadata.Read(ready);
        foreach (var key in new[] { "Author", "Description", "Creation Time", "Title", "Software", "Keywords" })
            Assert.AreEqual(originalFields[key], fields[key]);
        using var json = JsonDocument.Parse(fields["LOPATA"]);
        Assert.AreEqual(width, json.RootElement.GetProperty("Width").GetInt32());
        Assert.AreEqual(height, json.RootElement.GetProperty("Height").GetInt32());
        Assert.AreEqual(expectedWidth, json.RootElement.GetProperty("OutputWidth").GetInt32());
        Assert.AreEqual(expectedHeight, json.RootElement.GetProperty("OutputHeight").GetInt32());
        Assert.AreEqual(longest, json.RootElement.GetProperty("OutputLongestSide").GetInt32());
        Assert.AreEqual(42L, json.RootElement.GetProperty("Seed").GetInt64());
        Assert.AreEqual(request.Prompt, json.RootElement.GetProperty("Prompt").GetString());
        Assert.AreEqual(DateTimeOffset.Parse(originalFields["Creation Time"], System.Globalization.CultureInfo.InvariantCulture), json.RootElement.GetProperty("CreatedAt").GetDateTimeOffset());
        CollectionAssert.AreEqual(File.ReadAllBytes(ready), File.ReadAllBytes(result.ExportPath!));
        var originalBytes = File.ReadAllBytes(original); var readyBytes = File.ReadAllBytes(ready);
        var restored = JsonSerializer.Deserialize<ImageGenerationRequest>(JsonSerializer.Serialize(request))!;
        Assert.AreEqual(longest, restored.OutputLongestSide);
        // Recover a committed delivery intent without inference, recoding, or another exported copy.
        ImageGenerationSessionStore.UpdateResult(request, 0, current => current with { Exported = false });
        await runtime.RunAsync(restored, [], null, CancellationToken.None);
        Assert.AreEqual(1, worker.Calls); Assert.HasCount(1, Directory.GetFiles(request.OutputFolder));
        CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(original)); CollectionAssert.AreEqual(readyBytes, File.ReadAllBytes(ready));
    }

    [TestMethod]
    public async Task ProcessingFailureRetainsCanonicalAndRetryDoesNotGenerateAgain()
    {
        var request = Request(); var worker = new Worker(); var runtime = new ImageGenerationRuntime(worker);
        var output = ImageGenerationOutput.PathFor(request, 0); Directory.CreateDirectory(output);
        var failed = (await runtime.RunAsync(request, [], null, CancellationToken.None)).Results.Single();
        Assert.IsNotNull(failed.ProcessingError); Assert.IsFalse(failed.Exported);
        Assert.IsFalse(Directory.Exists(request.OutputFolder));
        var original = File.ReadAllBytes(ImageGenerationSessionStore.ResultPath(request, 0));
        Directory.Delete(output);
        var recovered = (await runtime.RunAsync(request, [], null, CancellationToken.None)).Results.Single();
        Assert.IsNull(recovered.ProcessingError); Assert.IsTrue(recovered.Exported); Assert.AreEqual(1, worker.Calls);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(ImageGenerationSessionStore.ResultPath(request, 0)));
        Assert.HasCount(0, Directory.GetFiles(request.SessionDirectory, "*.tmp"));
    }

    [TestMethod]
    public async Task CanceledResizingKeepsBothExistingFilesAndResumeUsesReadySource()
    {
        var request = Request(2048, 2048, 3840); var source = ImageGenerationSessionStore.ResultPath(request, 0);
        var worker = new Worker(); await worker.GenerateAsync(request, 0, [], "unused", source, CancellationToken.None);
        ImageGenerationSessionStore.AddTurn(request); var result = new ImageGenerationResult(0, Path.GetFileName(source), 42);
        ImageGenerationSessionStore.PutResult(request, result);
        var destination = Path.Combine(_root, "existing.png"); File.WriteAllText(destination, "keep existing result");
        var hash = SHA256.HashData(File.ReadAllBytes(source));
        using var canceled = new CancellationTokenSource(); canceled.CancelAfter(5);
        await Assert.ThrowsAsync<OperationCanceledException>(() => Task.Run(() =>
            ImageRasterScaler.SavePng(source, destination, 3840, 3840, new Dictionary<string, string>(), canceled.Token)));
        Assert.AreEqual("keep existing result", File.ReadAllText(destination));
        CollectionAssert.AreEqual(hash, SHA256.HashData(File.ReadAllBytes(source))); Assert.HasCount(0, Directory.GetFiles(_root, "*.tmp"));
        var recovered = await new ImageGenerationRuntime(worker).RunAsync(request, [], null, CancellationToken.None);
        Assert.AreEqual(1, worker.Calls); Assert.IsTrue(recovered.Results.Single().Exported);
    }

    [TestMethod]
    public async Task FailedExportRetriesPreparedOutputAndKeepsOtherUsersFile()
    {
        var request = Request(); var blocked = Path.Combine(_root, "blocked"); File.WriteAllText(blocked, "keep");
        request = request with { OutputFolder = blocked }; var worker = new Worker();
        var result = (await new ImageGenerationRuntime(worker).RunAsync(request, [], null, CancellationToken.None)).Results.Single();
        Assert.IsNull(result.ProcessingError); Assert.IsNotNull(result.ExportError); Assert.IsTrue(ImageGenerationOutput.IsReady(request, 0));
        var folder = Path.Combine(_root, "retry"); Directory.CreateDirectory(folder);
        var collision = Path.Combine(folder, ImageGenerationExport.FileName(request, 1)); File.WriteAllText(collision, "another file");
        var delivered = ImageGenerationExport.Save(request, result, folder);
        Assert.IsTrue(delivered.Exported); Assert.AreEqual("another file", File.ReadAllText(collision)); Assert.AreEqual(1, worker.Calls);
        CollectionAssert.AreEqual(File.ReadAllBytes(ImageGenerationOutput.PathFor(request, 0)), File.ReadAllBytes(delivered.ExportPath!));
    }

    [TestMethod]
    public async Task OriginalAndLegacySettingsDoNotChangeOrRecodeExistingPng()
    {
        var request = Request() with { OutputLongestSide = 0, Metadata = null };
        var worker = new Worker(); var runtime = new ImageGenerationRuntime(worker);
        var result = (await runtime.RunAsync(request, [], null, CancellationToken.None)).Results.Single();
        var source = ImageGenerationSessionStore.ResultPath(request, 0);
        Assert.AreEqual(source, ImageGenerationOutput.PathFor(request, 0));
        CollectionAssert.AreEqual(worker.LastBytes!, File.ReadAllBytes(source));
        CollectionAssert.AreEqual(worker.LastBytes!, File.ReadAllBytes(result.ExportPath!));
        Assert.HasCount(0, Directory.GetFiles(request.SessionDirectory, "*.output-*.png"));
        var legacy = JsonSerializer.Deserialize<ImageGenerationSettings>("{\"Folder\":\"images\"}")!;
        Assert.AreEqual(0, legacy.OutputLongestSide);
        var legacyRequest = JsonSerializer.Serialize(request).Replace(",\"OutputLongestSide\":0", "", StringComparison.Ordinal);
        Assert.AreEqual(0, JsonSerializer.Deserialize<ImageGenerationRequest>(legacyRequest)!.OutputLongestSide);
    }

    [TestMethod]
    public void StagedReductionSuppressesHighFrequencyAliasingAndKeepsTransparency()
    {
        Directory.CreateDirectory(_root); var source = Path.Combine(_root, "checker.png"); var target = Path.Combine(_root, "small.png");
        using var bitmap = new SKBitmap(512, 512);
        for (var y = 0; y < 512; y++) for (var x = 0; x < 512; x++) bitmap.SetPixel(x, y, (x + y) % 2 == 0 ? SKColors.White : SKColors.Black);
        using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using (var file = File.Create(source)) data.SaveTo(file);
        ImageRasterScaler.SavePng(source, target, 30, 30, new Dictionary<string, string>(), CancellationToken.None);
        using (var small = SKBitmap.Decode(target))
        { for (var y = 2; y < 28; y++) for (var x = 2; x < 28; x++) Assert.IsTrue(small.GetPixel(x, y).Red is > 100 and < 155); }
        bitmap.Erase(SKColors.Transparent); bitmap.SetPixel(256, 256, SKColors.Red);
        using var transparent = SKImage.FromBitmap(bitmap); using var transparentData = transparent.Encode(SKEncodedImageFormat.Png, 100);
        using (var file = File.Create(source)) transparentData.SaveTo(file);
        ImageRasterScaler.SavePng(source, target, 1024, 1024, new Dictionary<string, string>(), CancellationToken.None);
        using var alpha = SKBitmap.Decode(target); Assert.AreEqual((byte)0, alpha.GetPixel(0, 0).Alpha);
        Assert.AreEqual("Mitchell (staged reduction)", ImageRasterScaler.Algorithm(512, 512, 30, 30));
    }

    private sealed class Worker : IImageGenerationWorker
    {
        public int Calls { get; private set; }
        public byte[]? LastBytes { get; private set; }
        public Task GenerateAsync(ImageGenerationRequest request, int index, IReadOnlyList<ManagedModelArtifactCard> cards, string promptFile, string output, CancellationToken token)
        {
            Calls++;
            using var bitmap = new SKBitmap(request.Width, request.Height); using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.SlateBlue); using var paint = new SKPaint { Color = SKColors.Teal, IsAntialias = true };
            canvas.DrawCircle(request.Width / 2f, request.Height / 2f, Math.Min(request.Width, request.Height) / 3f, paint);
            using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            LastBytes = data.ToArray(); File.WriteAllBytes(output, LastBytes); return Task.CompletedTask;
        }
    }
}
