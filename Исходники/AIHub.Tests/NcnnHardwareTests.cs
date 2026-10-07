using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
[DoNotParallelize]
public sealed class NcnnHardwareTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void ArchiveModelsAreAllowedButEscapesAndWindowsAliasesAreRejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-archive-path-" + Guid.NewGuid().ToString("N"));
        Assert.AreEqual(Path.Combine(root, "models", "weights.bin"), PinnedZipExpansionVerifier.ResolveArchivePath(root, "models/weights.bin"));
        foreach (var relative in new[] { "../outside", "/rooted", "C:/outside", "models/../outside", "models\\weights", "models/weights:ads", "models/NUL.bin", "models/file.", "models/file ", "models//file" })
            Assert.Throws<InvalidDataException>(() => PinnedZipExpansionVerifier.ResolveArchivePath(root, relative), relative);
    }

    [TestMethod]
    public void BackendOrdinalsArePreservedAndUnsupportedSelectionsAreRejected()
    {
        var devices = NcnnDeviceProbe.Parse("[3 Intel Arc]  queueC=0[4]  queueG=0[16]\n[1 AMD Radeon]  queueC=2[8]\n[3 Intel Arc]  fp16-p/s/a=1/1/1\n[0 No Compute]  queueC=0[0]\n");
        CollectionAssert.AreEqual(new[] { 1, 3 }, devices.Select(device => device.Index).ToArray());
        Assert.AreEqual("Intel Arc", devices[1].Name);
        ImageUtilityAiService.ValidateNativeDevices("real-esrgan", "1,3", devices);
        ImageUtilityAiService.ValidateNativeDevices("real-cugan", "-1", []);
        ImageUtilityAiService.ValidateNativeDevices("real-cugan", "auto", []);
        Assert.Throws<ImageUtilityException>(() => ImageUtilityAiService.ValidateNativeDevices("real-esrgan", "auto", []));
        Assert.Throws<ImageUtilityException>(() => ImageUtilityAiService.ValidateNativeDevices("real-esrgan", "0", devices));
        Assert.Throws<InvalidDataException>(() => NcnnDeviceProbe.Parse("[0 A]  queueC=0[4]\n[0 B]  queueC=0[4]\n"));
    }

    [TestMethod]
    public async Task SameSizeExpansionCorruptionAndInjectedDllAreRejectedBeforeExecution()
    {
        var folder = Path.Combine(Path.GetTempPath(), "lopata-native-zip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var expanded = Path.Combine(folder, "expanded"); Directory.CreateDirectory(expanded);
        var source = Path.Combine(folder, "runtime.zip");
        using (var archive = ZipFile.Open(source, ZipArchiveMode.Create))
        { using var writer = new StreamWriter(archive.CreateEntry("worker.exe").Open()); writer.Write("original"); }
        ZipFile.ExtractToDirectory(source, expanded);
        var expected = new ManagedModelArtifactFile { SizeBytes = new FileInfo(source).Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))) };
        await PinnedZipExpansionVerifier.VerifyAsync(source, expected, expanded, CancellationToken.None);
        var worker = Path.Combine(expanded, "worker.exe");
        var timestamp = File.GetLastWriteTimeUtc(worker);
        File.WriteAllText(worker, "modified"); File.SetLastWriteTimeUtc(worker, timestamp);
        await Assert.ThrowsAsync<InvalidDataException>(() => PinnedZipExpansionVerifier.VerifyAsync(source, expected, expanded, CancellationToken.None));
        ZipFile.ExtractToDirectory(source, expanded, overwriteFiles: true);
        File.WriteAllText(Path.Combine(expanded, "injected.dll"), "foreign library");
        await Assert.ThrowsAsync<InvalidDataException>(() => PinnedZipExpansionVerifier.VerifyAsync(source, expected, expanded, CancellationToken.None));
    }

    [TestMethod]
    [DataRow("real-esrgan")]
    [DataRow("real-cugan")]
    public async Task PinnedNativePackageReportsItsOwnActualVulkanInventory(string method)
    {
        var root = Environment.GetEnvironmentVariable("AIHUB_NCNN_NATIVE_MODELS_ROOT");
        if (string.IsNullOrWhiteSpace(root)) Assert.Inconclusive("Explicit read-only original installed packages required.");
        var card = ImageUtilityAiCatalog.CreateCards(method, root).Single();
        var archive = card.Files.Single(file => file.Purpose == "runtime");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var expanded = Path.Combine(card.InstallDirectory, "expanded");
        await PinnedZipExpansionVerifier.VerifyAsync(Path.Combine(card.InstallDirectory, archive.RelativePath), archive, expanded, timeout.Token);
        var executable = Directory.GetFiles(expanded, "*ncnn-vulkan.exe", SearchOption.AllDirectories).Single();
        var devices = await NcnnDeviceProbe.ReadAsync(executable, timeout.Token);
        TestContext.WriteLine(System.Text.Json.JsonSerializer.Serialize(devices));
        if (Environment.GetEnvironmentVariable("AIHUB_NCNN_EXPECT_GPU") is { Length: > 0 } gpuName)
            Assert.IsTrue(devices.Any(device => device.Name.Contains(gpuName, StringComparison.Ordinal)));
        Assert.IsTrue(devices.All(device => device.Index is >= 0 and <= 15));
        await PinnedZipExpansionVerifier.VerifyAsync(Path.Combine(card.InstallDirectory, archive.RelativePath), archive, expanded, timeout.Token);
        await Assert.ThrowsAsync<OperationCanceledException>(() => NcnnDeviceProbe.ReadAsync(executable, new CancellationToken(true)));
    }

    [TestMethod]
    [DataRow("real-esrgan", "auto", 4)]
    [DataRow("real-cugan", "auto", 2)]
    [DataRow("real-cugan", "-1", 2)]
    public async Task OriginalNativeWeightsProduceValidSeparateImageThroughProductionService(string method, string device, int scale)
    {
        var root = Environment.GetEnvironmentVariable("AIHUB_NCNN_NATIVE_MODELS_ROOT");
        if (string.IsNullOrWhiteSpace(root)) Assert.Inconclusive("Explicit original native weights required.");
        var directory = Path.Combine(Path.GetTempPath(), "lopata-ncnn-service-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var store = new ManagedModelLibraryStore(Path.Combine(directory, "library"));
        using var service = new ImageUtilityAiService(store);
        service.ConfigureStorage(root);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var priorGate = ComponentLicenseGate.ConfirmAsync;
        var licenses = new HashSet<string>();
        ComponentLicenseGate.ConfirmAsync = (ids, token) => { token.ThrowIfCancellationRequested(); foreach (var id in ids) licenses.Add(id); return Task.CompletedTask; };
        try
        {
            Assert.IsTrue(await service.CheckAsync(method, null, timeout.Token));
            Assert.IsTrue(service.IsReady(method));
            await service.ListNativeDevicesAsync(method, timeout.Token);
            Assert.IsTrue(service.IsReady(method), "Inventory must preserve verification stamps.");
            var input = Path.Combine(directory, "source.png");
            using (var bitmap = new SkiaSharp.SKBitmap(8, 8))
            {
                bitmap.Erase(SkiaSharp.SKColors.CornflowerBlue);
                using var png = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(input, png.ToArray());
            }
            var original = File.ReadAllBytes(input);
            var output = Path.Combine(directory, "result.png");
            await service.ProcessAsync(method, input, output, new Dictionary<string, string>
                { ["device"] = device, ["tile"] = "32", ["scale"] = scale.ToString() }, null, timeout.Token);
            using var result = SkiaSharp.SKBitmap.Decode(output);
            Assert.IsNotNull(result);
            Assert.AreEqual(8 * scale, result.Width);
            Assert.AreEqual(8 * scale, result.Height);
            CollectionAssert.AreEqual(original, File.ReadAllBytes(input));
            Assert.IsTrue(service.IsReady(method));
            if (method == "real-cugan") Assert.IsTrue(licenses.Contains(ImageUtilityAiService.NcnnMsvcLicenseId));
            TestContext.WriteLine("Native outputs retained: " + directory);
        }
        finally { ComponentLicenseGate.ConfirmAsync = priorGate; }
    }
}
