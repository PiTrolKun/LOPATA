using System.IO;
using System.Security.Cryptography;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class SwinIrHardwareNativeTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task VerifiedWeightsProduceSeparateImagesOnCpuAndAutomaticDevice()
    {
        var weights = Environment.GetEnvironmentVariable("AIHUB_SWINIR_HARDWARE_WEIGHTS");
        var dependencies = Environment.GetEnvironmentVariable("AIHUB_SWINIR_HARDWARE_DEPENDENCIES");
        if (string.IsNullOrWhiteSpace(weights) || string.IsNullOrWhiteSpace(dependencies))
            Assert.Inconclusive("Opt-in native test requires verified weights and Pillow dependencies.");
        var entry = ImageUtilityAiCatalog.Artifacts.Single(a => a.MethodId == "swinir").Files.Single(f => f.Purpose == "scale-2");
        await using (var file = File.OpenRead(weights))
            Assert.AreEqual(entry.Sha256.ToUpperInvariant(), Convert.ToHexString(await SHA256.HashDataAsync(file)));
        var directory = Path.Combine(Path.GetTempPath(), "lopata-swin-hardware-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var input = Path.Combine(directory, "source.png");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        await GigaEmbeddingInstallation.RunAsync(["-c", "import sys; sys.path.insert(0,sys.argv[1]); from PIL import Image; Image.new('RGB',(8,8),(40,80,120)).save(sys.argv[2])",
            dependencies, input], TestContext.WriteLine, timeout.Token);
        var original = File.ReadAllBytes(input);
        foreach (var policy in new[] { "cpu", "auto" })
        {
            var output = Path.Combine(directory, policy + ".png");
            var lines = new List<string>();
            await GigaEmbeddingInstallation.RunAsync([Path.Combine(AppContext.BaseDirectory, "Tools", "image-utility-swinir.py"),
                "--dependencies", dependencies, "--weights", weights, "--input", input, "--output", output,
                "--scale", "2", "--tile", "8", "--overlap", "0", "--device", policy],
                line => { lock (lines) lines.Add(line); TestContext.WriteLine(line); }, timeout.Token);
            Assert.IsTrue(File.Exists(output));
            CollectionAssert.AreEqual(original, File.ReadAllBytes(input));
            if (policy == "auto" && Environment.GetEnvironmentVariable("AIHUB_SWINIR_EXPECT_GPU") == "1")
                Assert.IsTrue(lines.Any(line => line.StartsWith("LOPATA_DEVICE cuda:", StringComparison.Ordinal)));
            await GigaEmbeddingInstallation.RunAsync(["-c", "import sys; sys.path.insert(0,sys.argv[1]); from PIL import Image; im=Image.open(sys.argv[2]); im.load(); assert im.size==(16,16); print('VALID_IMAGE',im.size)",
                dependencies, output], TestContext.WriteLine, timeout.Token);
        }
        TestContext.WriteLine("Separate checked outputs retained in " + directory);
    }
}
