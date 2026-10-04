using System.Diagnostics;
using System.Security.Cryptography;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class ImageUtilityTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LOPATA-ImageUtility-tests", Guid.NewGuid().ToString("N"));
    private string Magick => Path.Combine(AppContext.BaseDirectory, "ImageUtilityRuntime", "ImageMagick", "magick.exe");

    [TestInitialize]
    public void Initialize() => Directory.CreateDirectory(_root);

    [TestCleanup]
    public void Cleanup()
    {
        var expectedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "LOPATA-ImageUtility-tests")) + Path.DirectorySeparatorChar;
        Assert.StartsWith(expectedRoot, Path.GetFullPath(_root));
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public void PresetsRespectPortraitLandscapeAndSquare()
    {
        Assert.AreEqual((1920, 1080), ImageUtilityDimensions.Calculate(1600, 900, 1080));
        Assert.AreEqual((1080, 1920), ImageUtilityDimensions.Calculate(900, 1600, 1080));
        Assert.AreEqual((1920, 1920), ImageUtilityDimensions.Calculate(1024, 1024, 1080));
        Assert.AreEqual((7680, 5120), ImageUtilityDimensions.Calculate(600, 400, 4320));
        Assert.AreEqual((123, 77), ImageUtilityDimensions.Calculate(123, 77, new ImageUtilityOptions { FormatOnly = true }));
    }

    [TestMethod]
    public async Task SourcesExpandOnlyRequestedFoldersAndKeepDuplicatesVisible()
    {
        var folder = Path.Combine(_root, "source"); Directory.CreateDirectory(folder);
        var child = Path.Combine(folder, "child"); Directory.CreateDirectory(child);
        File.WriteAllText(Path.Combine(folder, "a.png"), "a");
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "not an image");
        File.WriteAllText(Path.Combine(child, "a.png"), "b");
        var job = new ImageUtilityJob();
        await ImageUtilitySources.AddAsync(job, [folder], false);
        Assert.HasCount(1, job.Items);
        await ImageUtilitySources.AddAsync(job, [folder], true);
        Assert.HasCount(3, job.Items);
        Assert.AreEqual(ImageUtilityItemStatus.Duplicate, job.Items[1].Status);
        Assert.AreEqual(ImageUtilityItemStatus.Pending, job.Items[2].Status);
        Assert.AreEqual("child", job.Items[2].RelativeFolder);
    }

    [TestMethod]
    public async Task FailedFileRetriesThreeTimesThenQueueContinues()
    {
        var attempts = new Dictionary<string, int>();
        var processor = new FakeProcessor((item, _, folder, _, _, _) =>
        {
            attempts[item.Source] = attempts.GetValueOrDefault(item.Source) + 1;
            if (item.Source.EndsWith("bad.png", StringComparison.Ordinal)) throw new IOException("broken");
            var output = Path.Combine(folder, "good.png"); File.WriteAllText(output, "result");
            return Task.FromResult(output);
        });
        var store = new ImageUtilityStore(Path.Combine(_root, "store"));
        var job = Job("bad.png", "good.png", "good.png");
        await new ImageUtilityQueue(processor, store).RunAsync(job);
        Assert.AreEqual(4, attempts[job.Items[0].Source]);
        Assert.AreEqual(1, attempts[job.Items[1].Source]);
        Assert.AreEqual(ImageUtilityItemStatus.Failed, job.Items[0].Status);
        Assert.AreEqual(ImageUtilityItemStatus.Completed, job.Items[1].Status);
        Assert.AreEqual(ImageUtilityItemStatus.Duplicate, job.Items[2].Status);
        Assert.IsTrue(job.OutputFolder!.Contains("_Апскейл_000001", StringComparison.Ordinal));
        Assert.AreEqual(1L, new ImageUtilityStore(Path.Combine(_root, "store")).LoadPreferences().LastProcessNumber);
        await new ImageUtilityQueue(processor, store).RunAsync(job);
        Assert.AreEqual(1, attempts[job.Items[1].Source]);
    }

    [TestMethod]
    public async Task PauseRestartsOnlyUnfinishedFileWithoutSpendingRetry()
    {
        using var cancellation = new CancellationTokenSource();
        var completedCalls = 0;
        var store = new ImageUtilityStore(Path.Combine(_root, "store"));
        var job = Job("first.png", "second.png");
        var firstRun = new FakeProcessor((item, _, folder, _, token, _) =>
        {
            if (item == job.Items[0]) { completedCalls++; return Task.FromResult(Path.Combine(folder, "done.png")); }
            cancellation.Cancel(); token.ThrowIfCancellationRequested(); return Task.FromResult("");
        });
        await Assert.ThrowsAsync<OperationCanceledException>(() => new ImageUtilityQueue(firstRun, store).RunAsync(job, token: cancellation.Token));
        var restored = store.LoadJob(job.Id)!;
        Assert.AreEqual(ImageUtilityItemStatus.Completed, restored.Items[0].Status);
        Assert.AreEqual(ImageUtilityItemStatus.Pending, restored.Items[1].Status);
        Assert.AreEqual(0, restored.Items[1].Attempts);
        var secondRun = new FakeProcessor((item, _, folder, _, _, _) =>
        {
            if (item.Source == job.Items[0].Source) completedCalls++;
            return Task.FromResult(Path.Combine(folder, "done2.png"));
        });
        await new ImageUtilityQueue(secondRun, store).RunAsync(restored);
        Assert.AreEqual(1, completedCalls);
        Assert.AreEqual(ImageUtilityItemStatus.Completed, restored.Items[1].Status);
    }

    [TestMethod]
    public async Task CrashAfterPublicationRecoversByHashWithoutRegeneration()
    {
        var store = new ImageUtilityStore(Path.Combine(_root, "store"));
        var job = Job("source.png");
        var result = Path.Combine(_root, "already-published.png"); File.WriteAllText(result, "verified bytes");
        job.Items[0].Status = ImageUtilityItemStatus.Running;
        job.Items[0].PlannedOutputPath = result;
        job.Items[0].PreparedOutputSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(result)));
        job.Items[0].Attempts = 1; store.SaveJob(job);
        var calls = 0;
        var processor = new FakeProcessor((_, _, _, _, _, _) => { calls++; throw new InvalidOperationException(); });
        await new ImageUtilityQueue(processor, store).RunAsync(store.LoadJob(job.Id)!);
        Assert.AreEqual(0, calls);
        Assert.AreEqual(ImageUtilityItemStatus.Completed, store.LoadJob(job.Id)!.Items[0].Status);
    }

    [TestMethod]
    public void FormatDiscoveryExcludesPseudoCodersAndOutputTraversal()
    {
        var formats = ImageUtilityFormats.FromRuntimeList(" PNG* rw- Portable\n WEBP* rw+ WebP\n HEIC r-- HEIC\n HTTP* r-- URL\n INFO -w+ Metadata\n");
        CollectionAssert.AreEquivalent(new[] { "png", "webp" }, formats.Select(x => x.Id).ToArray());
        Assert.Throws<ImageUtilityException>(() => ImageUtilityProcessor.ResolveOutputFolder(_root, "..\\outside"));
        Assert.AreEqual("_CON", ImageUtilityProcessor.SafeFileName("CON"));
    }

    [TestMethod]
    public async Task NativeClassicMethodsPreserveSourceAuthorAndExactAspect()
    {
        var source = Path.Combine(_root, "original[100%].png");
        Run("-size", "800x600", "gradient:red-blue", "-set", "Author", "Original author", "-set", "comment", "Source comment", source);
        var hash = SHA256.HashData(File.ReadAllBytes(source));
        var processor = new ImageUtilityProcessor(Magick);
        foreach (var method in ImageUtilityCatalog.Methods.Where(x => !x.IsAi))
        {
            var item = new ImageUtilityItem { Source = source, DisplayName = Path.GetFileName(source) };
            var result = await processor.ProcessAsync(item, new() { MethodId = method.Id, Preset = 360,
                Parameters = ImageUtilityCatalog.RecommendedParameters(method.Id) }, _root);
            var info = await processor.InspectAsync(result);
            Assert.AreEqual((640, 480), (info.Width, info.Height), method.Id);
            Assert.AreEqual("Original author", Run("identify", "-format", "%[Author]", result));
            Assert.Contains("Source comment", Run("identify", "-format", "%[comment]", result));
            Assert.AreNotEqual(source, result);
        }
        CollectionAssert.AreEqual(hash, SHA256.HashData(File.ReadAllBytes(source)));
    }

    [TestMethod]
    public async Task NativeFormatOnlyKeepsDimensionsAndDoesNotInvokeAi()
    {
        var source = Path.Combine(_root, "source.png"); Run("-size", "64x48", "xc:blue", source);
        var processor = new ImageUtilityProcessor(Magick);
        foreach (var format in await processor.GetFormatsAsync())
        {
            var item = new ImageUtilityItem { Source = source, DisplayName = "converted" };
            var result = await processor.ProcessAsync(item, new() { MethodId = "real-esrgan", FormatOnly = true, Format = format.Id }, _root);
            var info = await processor.InspectAsync(result);
            Assert.AreEqual((64, 48), (info.Width, info.Height), format.Id);
        }
    }

    [TestMethod]
    public async Task NativeOrientationKeepsVisibleShapeAndFormatOnlyResolution()
    {
        var source = Path.Combine(_root, "rotated.tif");
        Run("-size", "80x60", "gradient:red-blue", "-orient", "RightTop", source);
        var processor = new ImageUtilityProcessor(Magick);
        var sourceInfo = await processor.InspectAsync(source);
        Assert.AreEqual((60, 80), (sourceInfo.Width, sourceInfo.Height));
        foreach (var formatOnly in new[] { true, false })
        {
            var item = new ImageUtilityItem { Source = source, DisplayName = "rotated" };
            var result = await processor.ProcessAsync(item, new() { FormatOnly = formatOnly, Preset = 360 }, _root);
            var resultInfo = await processor.InspectAsync(result);
            Assert.AreEqual(formatOnly ? (60, 80) : (480, 640), (resultInfo.Width, resultInfo.Height));
        }
    }

    [TestMethod]
    public async Task NativeAnimationsKeepTimingLoopAndTransparentFrames()
    {
        var source = Path.Combine(_root, "source.gif");
        Run("-size", "30x20", "-delay", "13", "-dispose", "Background", "xc:none", "-fill", "red", "-draw", "rectangle 0,0 12,12",
            "-delay", "29", "-dispose", "Previous", "xc:none", "-fill", "blue", "-draw", "rectangle 12,7 25,18", "-loop", "3", source);
        foreach (var format in new[] { "gif", "webp", "mng" })
        {
            var item = new ImageUtilityItem { Source = source, DisplayName = "animation" };
            var result = await new ImageUtilityProcessor(Magick).ProcessAsync(item, new() { Preset = 360, Format = format }, _root);
            var verbose = Run("identify", "-verbose", result);
            Assert.Contains("Delay: 13x100", verbose, format);
            Assert.Contains("Delay: 29x100", verbose, format);
            Assert.Contains("Iterations: 3", verbose, format);
            Assert.Contains("Alpha:", verbose, format);
        }
    }

    [TestMethod]
    public async Task AiPostprocessingRestoresOriginalAlphaAndAuthor()
    {
        var source = Path.Combine(_root, "alpha.png");
        Run("-size", "32x32", "xc:none", "-fill", "red", "-draw", "rectangle 8,8 24,24", "-set", "Author", "Original author", source);
        var ai = new FakeAi((input, output) => Run(input, "-background", "white", "-alpha", "remove", "-alpha", "off", output));
        var processor = new ImageUtilityProcessor(Magick, ai);
        var item = new ImageUtilityItem { Source = source, DisplayName = "ai-alpha" };
        var output = await processor.ProcessAsync(item, new() { MethodId = "real-esrgan", Preset = 360 }, _root);
        Assert.AreEqual("Original author", Run("identify", "-format", "%[Author]", output));
        Assert.AreEqual("0", Run("identify", "-format", "%[fx:p{0,0}.a]", output));
        Assert.AreEqual("1", Run("identify", "-format", "%[fx:p{320,320}.a]", output));
    }

    private ImageUtilityJob Job(params string[] names) => new()
    {
        Options = new() { ExportFolder = Path.Combine(_root, "output") },
        Items = names.Select(x => new ImageUtilityItem { Source = Path.Combine(_root, x), DisplayName = x }).ToList()
    };

    private string Run(params string[] arguments)
    {
        Assert.IsTrue(File.Exists(Magick), "Bundled ImageMagick must be copied to test output.");
        var start = new ProcessStartInfo(Magick) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd();
        process.WaitForExit(); Assert.AreEqual(0, process.ExitCode, error); return output;
    }

    private sealed class FakeProcessor(Func<ImageUtilityItem, ImageUtilityOptions, string, IProgress<ImageUtilityProgress>?, CancellationToken, Action?, Task<string>> process) : IImageUtilityProcessor
    {
        public Task<string> ProcessAsync(ImageUtilityItem item, ImageUtilityOptions options, string outputFolder,
            IProgress<ImageUtilityProgress>? progress = null, CancellationToken token = default, Action? checkpoint = null)
            => process(item, options, outputFolder, progress, token, checkpoint);
    }

    private sealed class FakeAi(Action<string, string> process) : IImageUtilityAiProcessor
    {
        public Task ProcessAsync(string methodId, string inputPath, string outputPngPath, IReadOnlyDictionary<string, string> parameters,
            IProgress<ImageUtilityProgress>? progress, CancellationToken token)
        { process(inputPath, outputPngPath); return Task.CompletedTask; }
    }
}
