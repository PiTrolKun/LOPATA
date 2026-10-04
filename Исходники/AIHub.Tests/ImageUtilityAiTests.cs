using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class ImageUtilityAiTests
{
    [TestMethod]
    public void NcnnPhotographicWeightsRejectUnsupportedScale()
    {
        Assert.Throws<ImageUtilityException>(() => Arguments("real-esrgan", new() { ["scale"] = "2" }));
        var anime = Arguments("real-esrgan", new() { ["model"] = "realesr-animevideov3", ["scale"] = "2" });
        CollectionAssert.Contains(anime.ToArray(), "realesr-animevideov3");
    }

    [TestMethod]
    public void CuganRejectsMissingWeightCombinations()
    {
        Assert.Throws<ImageUtilityException>(() => Arguments("real-cugan", new() { ["model"] = "models-pro", ["scale"] = "4" }));
        Assert.Throws<ImageUtilityException>(() => Arguments("real-cugan", new() { ["scale"] = "3", ["noise"] = "1" }));
        Assert.Throws<ImageUtilityException>(() => Arguments("real-cugan", new() { ["model"] = "models-nose" }));
        var valid = Arguments("real-cugan", new() { ["model"] = "models-nose", ["noise"] = "0" });
        CollectionAssert.Contains(valid.ToArray(), System.IO.Path.Combine("C:\\native", "models-nose"));
    }

    [TestMethod]
    public void MultiGpuUsesArgumentListValuesAndValidatesCardinality()
    {
        var settings = new Dictionary<string, string> { ["device"] = "0,1", ["tile"] = "64,128", ["threads"] = "1:2,2:2", ["tta"] = "true" };
        var args = Arguments("real-esrgan", settings);
        CollectionAssert.Contains(args.ToArray(), "0,1");
        CollectionAssert.Contains(args.ToArray(), "-x");
        CollectionAssert.Contains(args.ToArray(), "C:\\image inputs\\input.png");
        settings["tile"] = "64";
        Assert.Throws<ImageUtilityException>(() => Arguments("real-esrgan", settings));
        Assert.Throws<ImageUtilityException>(() => Arguments("real-esrgan", new() { ["threads"] = "1:2 & calc:2" }));
    }

    [TestMethod]
    public void EverySelectableAiMethodHasPinnedArtifactsAndValidDefaults()
    {
        foreach (var method in new[] { "real-esrgan", "real-cugan", "swinir" })
        {
            var artifacts = ImageUtilityAiCatalog.Artifacts.Where(a => a.MethodId == method).ToArray();
            Assert.IsNotEmpty(artifacts);
            foreach (var file in artifacts.SelectMany(a => a.Files))
            {
                Assert.AreEqual(64, file.Sha256.Length);
                Assert.IsTrue(file.Sha256.All(Uri.IsHexDigit));
                Assert.IsGreaterThan(0L, file.SizeBytes);
                Assert.StartsWith("https://", file.SourceUrl);
            }
            var defaults = ImageUtilityAiCatalog.Settings(method).ToDictionary(s => s.Key, s => s.DefaultValue);
            if (method != "swinir") Assert.IsNotEmpty(Arguments(method, defaults));
        }
        CollectionAssert.AreEquivalent(new[] { "scale-2", "scale-3", "scale-4", "scale-8" },
            ImageUtilityAiCatalog.Artifacts.Single(a => a.MethodId == "swinir").Files
                .Where(f => f.Purpose.StartsWith("scale-", StringComparison.Ordinal)).Select(f => f.Purpose).ToArray());
    }

    private static IReadOnlyList<string> Arguments(string method, Dictionary<string, string> parameters)
        => ImageUtilityAiService.BuildNativeArguments(method, "C:\\native", "C:\\image inputs\\input.png", "C:\\outputs\\result.png", parameters);
}
