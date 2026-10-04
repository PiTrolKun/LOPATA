using System.Diagnostics;
using System.Security.Cryptography;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class ImageUtilityPolishTests
{
    [TestMethod]
    public async Task SeriesNamingProducesSequentialSeparateOutputsWithoutOverwrite()
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-polish-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var processor = new ImageUtilityProcessor(); var source = Path.Combine(root, "source.png");
            var info = new ProcessStartInfo(processor.ExecutablePath) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "-size", "16x12", "xc:blue", source }) info.ArgumentList.Add(argument);
            using (var process = Process.Start(info)!) { await process.WaitForExitAsync(); Assert.AreEqual(0, process.ExitCode); }
            var hash = SHA256.HashData(File.ReadAllBytes(source));
            var options = new ImageUtilityOptions { FormatOnly = true, Format = "png", NamingMode = "series", CustomName = "Series" };
            var first = await processor.ProcessAsync(new() { Source = source, LocalPath = source, DisplayName = "source.png" }, options, root);
            var existing = File.ReadAllBytes(first);
            var second = await processor.ProcessAsync(new() { Source = source, LocalPath = source, DisplayName = "source.png" }, options, root);
            Assert.AreEqual("Series_0001.png", Path.GetFileName(first)); Assert.AreEqual("Series_0002.png", Path.GetFileName(second));
            CollectionAssert.AreEqual(existing, File.ReadAllBytes(first)); CollectionAssert.AreEqual(hash, SHA256.HashData(File.ReadAllBytes(source)));
            options.NamingMode = "method";
            Assert.AreEqual("source_convert", ImageUtilityNaming.CreateStem(new() { DisplayName = "source.png" }, options));
            options.FormatOnly = false; Assert.AreEqual("source_lanczos3", ImageUtilityNaming.CreateStem(new() { DisplayName = "source.png" }, options));
        }
        finally { Directory.Delete(root, true); }
    }
}
