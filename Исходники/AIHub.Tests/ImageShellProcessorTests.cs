using System.Diagnostics;
using System.Security.Cryptography;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class ImageShellProcessorTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LOPATA-ImageShell-tests", Guid.NewGuid().ToString("N"));
    private string Magick => Path.Combine(AppContext.BaseDirectory, "ImageUtilityRuntime", "ImageMagick", "magick.exe");
    private string Transactions => Path.Combine(_root, "transactions");
    private ImageUtilityProcessor Native => new(Magick);

    [TestInitialize]
    public void Initialize() => Directory.CreateDirectory(_root);

    [TestCleanup]
    public void Cleanup()
    {
        var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "LOPATA-ImageShell-tests")) + Path.DirectorySeparatorChar;
        Assert.StartsWith(allowed, Path.GetFullPath(_root));
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public async Task UpscaleReplacesSameFileAndReceiptPreventsAnotherDoubling()
    {
        var input = await Image("image.png");
        var request = Request(ImageShellOperation.Upscale2, input);
        var processor = new ImageShellProcessor(Native);
        var first = await processor.ProcessAsync(request, input, Transactions);
        Assert.AreEqual(input, first.OutputPath);
        Assert.IsTrue(first.Changed);
        Assert.AreEqual((64, 40), await Size(input));
        var firstHash = Hash(input);
        var second = await processor.ProcessAsync(request, input, Transactions);
        Assert.IsTrue(second.Recovered);
        Assert.AreEqual(firstHash, Hash(input));
        Assert.AreEqual((64, 40), await Size(input));
        Assert.HasCount(1, Directory.GetFiles(_root));
    }

    [TestMethod]
    public async Task WebPIsLosslessKeepsExistingDestinationAndRemovesSourceAfterPublication()
    {
        var input = await Image("image.png");
        var occupied = await Image("image.webp", "blue");
        var occupiedHash = Hash(occupied);
        var result = await new ImageShellProcessor(Native).ProcessAsync(Request(ImageShellOperation.WebP, input), input, Transactions);
        Assert.AreEqual(Path.Combine(_root, "image_1.webp"), result.OutputPath);
        Assert.IsFalse(File.Exists(input));
        Assert.AreEqual(occupiedHash, Hash(occupied));
        Assert.AreEqual((32, 20), await Size(result.OutputPath));
        Assert.StartsWith("FF0000", (await Run(result.OutputPath, "-format", "%[hex:p{0,0}]", "info:")).Trim());
    }

    [TestMethod]
    public async Task ExistingWebPIsAnUnchangedNoOp()
    {
        var input = await Image("already.webp"); var hash = Hash(input);
        var request = Request(ImageShellOperation.WebP, input);
        var result = await new ImageShellProcessor(Native).ProcessAsync(request, input, Transactions);
        Assert.IsFalse(result.Changed);
        Assert.AreEqual(input, result.OutputPath);
        Assert.AreEqual(hash, Hash(input));
        Assert.IsFalse((await new ImageShellProcessor(Native).ProcessAsync(request, input, Transactions)).Changed);
    }

    [TestMethod]
    public async Task InvalidOrLockedSourceIsNeverRemovedOrChanged()
    {
        var invalid = Path.Combine(_root, "broken.png"); File.WriteAllText(invalid, "not an image");
        var before = Hash(invalid);
        await ExpectFailure(() => new ImageShellProcessor(Native).ProcessAsync(Request(ImageShellOperation.WebP, invalid), invalid, Transactions));
        Assert.AreEqual(before, Hash(invalid));
        var input = await Image("locked.png"); before = Hash(input);
        using (var locked = new FileStream(input, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await ExpectFailure(() => new ImageShellProcessor(Native).ProcessAsync(Request(ImageShellOperation.Upscale2, input), input, Transactions));
        Assert.AreEqual(before, Hash(input));
    }

    [TestMethod]
    public async Task ChangedSourceAfterPreparedCancellationIsRefused()
    {
        var input = await Image("changed.png"); var request = Request(ImageShellOperation.Upscale2, input);
        using var cancellation = new CancellationTokenSource();
        var interrupted = new ImageShellProcessor(Native, state => { if (state == "prepared") cancellation.Cancel(); });
        await Assert.ThrowsAsync<OperationCanceledException>(() => interrupted.ProcessAsync(request, input, Transactions, cancellation.Token));
        await Run("-size", "10x15", "xc:blue", input);
        var changed = Hash(input);
        var exception = await Assert.ThrowsAsync<ImageUtilityException>(() => new ImageShellProcessor(Native).ProcessAsync(request, input, Transactions));
        Assert.AreEqual("ImageShell.Error.SourceChanged", exception.MessageKey);
        Assert.AreEqual(changed, Hash(input));
    }

    [TestMethod]
    public async Task InterruptedRenameRecoversOriginalBackupAndAppliesExactlyOnce()
    {
        var input = await Image("gap.png"); var request = Request(ImageShellOperation.Upscale2, input);
        var interrupted = new ImageShellProcessor(Native, state => { if (state == "source-moved") throw new SimulatedCrash(); });
        await Assert.ThrowsAsync<SimulatedCrash>(() => interrupted.ProcessAsync(request, input, Transactions));
        Assert.IsFalse(File.Exists(input));
        Assert.HasCount(1, Directory.GetFiles(_root, "original.backup", SearchOption.AllDirectories));
        var result = await new ImageShellProcessor(Native).ProcessAsync(request, input, Transactions);
        Assert.AreEqual(input, result.OutputPath);
        Assert.AreEqual((64, 40), await Size(input));
        Assert.IsFalse(Directory.GetFiles(_root, "original.backup", SearchOption.AllDirectories).Any());
    }

    [TestMethod]
    public async Task PublishedBeforeReceiptIsRecognizedWithoutRegeneration()
    {
        foreach (var operation in new[] { ImageShellOperation.Upscale2, ImageShellOperation.WebP })
        {
            var input = await Image(operation + ".png"); var request = Request(operation, input);
            var interrupted = new ImageShellProcessor(Native, state => { if (state == "published") throw new SimulatedCrash(); });
            await Assert.ThrowsAsync<SimulatedCrash>(() => interrupted.ProcessAsync(request, input, Transactions));
            var result = await new ImageShellProcessor(Native).ProcessAsync(request, input, Transactions);
            Assert.IsTrue(result.Recovered);
            Assert.AreEqual(operation == ImageShellOperation.Upscale2 ? (64, 40) : (32, 20), await Size(result.OutputPath));
            if (operation == ImageShellOperation.WebP) Assert.IsFalse(File.Exists(input));
        }
    }

    [TestMethod]
    public async Task CompetingFileIsNotOverwrittenAndOriginalBackupIsRetained()
    {
        var input = await Image("competition.png"); var originalHash = Hash(input);
        var request = Request(ImageShellOperation.Upscale2, input);
        var processor = new ImageShellProcessor(Native, state =>
        {
            if (state == "source-moved") File.WriteAllText(input, "new file from another program");
        });
        await ExpectFailure(() => processor.ProcessAsync(request, input, Transactions));
        Assert.AreEqual("new file from another program", File.ReadAllText(input));
        var backup = Directory.GetFiles(_root, "original.backup", SearchOption.AllDirectories).Single();
        Assert.AreEqual(originalHash, Hash(backup));
        await ExpectFailure(() => new ImageShellProcessor(Native).ProcessAsync(request, input, Transactions));
        Assert.AreEqual("new file from another program", File.ReadAllText(input));
        Assert.AreEqual(originalHash, Hash(backup));
    }

    private async Task<string> Image(string name, string color = "red")
    {
        var path = Path.Combine(_root, name);
        await Run("-size", "32x20", "xc:" + color, "-define", "webp:lossless=true", path);
        return path;
    }

    private static ImageShellRequest Request(ImageShellOperation operation, string input) => new(Guid.NewGuid().ToString("N"), operation, [input]);
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private async Task<(int, int)> Size(string path) { var info = await Native.InspectAsync(path); return (info.Width, info.Height); }
    private static async Task ExpectFailure(Func<Task> operation)
    {
        try { await operation(); Assert.Fail("Expected the operation to fail."); }
        catch (AssertFailedException) { throw; }
        catch (Exception) { }
    }

    private async Task<string> Run(params string[] arguments)
    {
        var start = new ProcessStartInfo(Magick) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.AreEqual(0, process.ExitCode, await error);
        return await output;
    }

    private sealed class SimulatedCrash : Exception;
}
