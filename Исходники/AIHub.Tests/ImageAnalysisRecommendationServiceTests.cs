using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class ImageAnalysisRecommendationServiceTests
{
    private readonly ImageAnalysisRecommendationService _service = new();

    [TestMethod]
    public void Evaluate_RecommendsLight_WhenOnlyLightMatches()
    {
        var result = Evaluate(16, 8, 8, 20);

        Assert.AreEqual(ImageAnalysisBundleCatalog.LightId, result.Recommendation?.Bundle.Id);
        Assert.IsTrue(result.IsComfortableMatch);
    }

    [TestMethod]
    public void Evaluate_RecommendsMedium_WhenMediumButNotHeavyMatches()
    {
        var result = Evaluate(40, 16, 12, 40);

        Assert.AreEqual(ImageAnalysisBundleCatalog.MediumId, result.Recommendation?.Bundle.Id);
        Assert.IsTrue(result.IsComfortableMatch);
    }

    [TestMethod]
    public void Evaluate_RecommendsHeavy_ForPowerfulUserClassPc()
    {
        var result = Evaluate(128, 24, 32, 100);

        Assert.AreEqual(ImageAnalysisBundleCatalog.HeavyId, result.Recommendation?.Bundle.Id);
        Assert.IsTrue(result.IsComfortableMatch);
    }

    [TestMethod]
    public void Evaluate_TreatsExactHeavyThresholdsAsCompatible()
    {
        var result = Evaluate(32, 24, 12, 20);

        Assert.AreEqual(ImageAnalysisBundleCatalog.HeavyId, result.Recommendation?.Bundle.Id);
        Assert.IsTrue(result.Recommendation?.IsFullyCompatible);
        Assert.AreEqual(0, result.Recommendation?.TotalNormalizedDeficit);
    }

    [TestMethod]
    public void Evaluate_ToleratesReportedVramRoundingAtNominalThreshold()
    {
        var result = Evaluate(127.76, 23.99, 32, 100);

        Assert.AreEqual(ImageAnalysisBundleCatalog.HeavyId, result.Recommendation?.Bundle.Id);
        Assert.IsTrue(result.Recommendation?.IsFullyCompatible);
    }

    [TestMethod]
    public void Evaluate_ReportsSingleResourceBelowThreshold()
    {
        var result = Evaluate(32, 23, 12, 16);
        var heavy = result.Assessments.Single(assessment =>
            assessment.Bundle.Id == ImageAnalysisBundleCatalog.HeavyId);
        var vram = heavy.Resources.Single(resource =>
            resource.ResourceId == ImageAnalysisRecommendationService.VramResourceId);

        Assert.IsFalse(heavy.IsFullyCompatible);
        Assert.IsFalse(vram.IsSatisfied);
        Assert.IsTrue(vram.NormalizedDeficit > 0);
    }

    [TestMethod]
    public void Evaluate_ReturnsClosestBundle_WhenNoneFullyMatches()
    {
        var result = Evaluate(8, 2, 4, 10);

        Assert.AreEqual(ImageAnalysisBundleCatalog.LightId, result.Recommendation?.Bundle.Id);
        Assert.IsFalse(result.IsComfortableMatch);
    }

    [TestMethod]
    public void Evaluate_PrefersLighterBundle_WhenNormalizedDeficitIsEqual()
    {
        var bundles = new[]
        {
            CreateBundle("lighter", 1, 16, 8, 8, 20),
            CreateBundle("heavier", 2, 16, 8, 8, 20)
        };
        var result = _service.Evaluate(
            bundles,
            Snapshot(8, 4, 4, 10));

        Assert.AreEqual("lighter", result.Recommendation?.Bundle.Id);
    }

    [TestMethod]
    public void Evaluate_DoesNotGuess_WhenVramIsUnknown()
    {
        var result = _service.Evaluate(
            ImageAnalysisBundleCatalog.Create(),
            Snapshot(64, null, 16, 50));

        Assert.IsFalse(result.HasCompleteHardwareData);
        Assert.IsNull(result.Recommendation);
    }

    [TestMethod]
    public void Evaluate_DoesNotGuess_WhenModelsStorageIsUnknown()
    {
        var result = _service.Evaluate(
            ImageAnalysisBundleCatalog.Create(),
            Snapshot(64, 24, 16, null));

        Assert.IsFalse(result.HasCompleteHardwareData);
        Assert.IsNull(result.Recommendation);
    }

    [TestMethod]
    public void OlderPassportJson_RemainsReadableWithoutLogicalProcessorCount()
    {
        const string json = """
            {
              "createdAt": "2026-08-22T00:00:00+07:00",
              "machineName": "legacy",
              "ramTotalGb": 32,
              "gpus": [],
              "drives": []
            }
            """;

        var passport = JsonSerializer.Deserialize<ComputerPassport>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        Assert.IsNotNull(passport);
        Assert.AreEqual(0, passport.LogicalProcessorCount);
        var result = _service.Evaluate(
            ImageAnalysisBundleCatalog.Create(),
            Snapshot(passport.RamTotalGb, 16, null, 50));
        Assert.IsNull(result.Recommendation);
    }

    [TestMethod]
    public void Evaluate_CanRecommendAvailableHeavyBundle()
    {
        var result = Evaluate(128, 24, 32, 100);

        Assert.AreEqual(ImageAnalysisBundleCatalog.HeavyId, result.Recommendation?.Bundle.Id);
        Assert.IsTrue(result.Recommendation?.Bundle.IsAvailable);
    }

    [TestMethod]
    public void Catalog_KeepsMediumAsCurrentProjectBundleRegardlessOfRecommendation()
    {
        var bundles = ImageAnalysisBundleCatalog.Create();
        var result = _service.Evaluate(bundles, Snapshot(128, 24, 32, 100));
        var current = bundles.Single(bundle => bundle.IsCurrentProjectBundle);

        Assert.AreEqual(ImageAnalysisBundleCatalog.MediumId, current.Id);
        Assert.AreEqual(ImageAnalysisBundleCatalog.HeavyId, result.Recommendation?.Bundle.Id);
    }

    [TestMethod]
    public void HardwareSnapshot_UsesLargestSingleGpuInsteadOfSummingVram()
    {
        var passport = new ComputerPassport
        {
            RamTotalGb = 32,
            LogicalProcessorCount = 12,
            Gpus =
            [
                new GpuPassport { Name = "GPU 1", VramGb = 8 },
                new GpuPassport { Name = "GPU 2", VramGb = 12 }
            ]
        };

        var snapshot = new ImageAnalysisHardwareSnapshotService().Create(
            passport,
            new StorageSettings());

        Assert.AreEqual(12, snapshot.VramGb);
        Assert.IsNull(snapshot.FreeDiskGb);
    }

    private ImageAnalysisRecommendationResult Evaluate(
        double ram,
        double vram,
        int cpu,
        double disk) =>
        _service.Evaluate(
            ImageAnalysisBundleCatalog.Create(),
            Snapshot(ram, vram, cpu, disk));

    private static ImageAnalysisHardwareSnapshot Snapshot(
        double? ram,
        double? vram,
        int? cpu,
        double? disk) =>
        new()
        {
            RamGb = ram,
            VramGb = vram,
            LogicalProcessorCount = cpu,
            FreeDiskGb = disk
        };

    private static ImageAnalysisBundleDefinition CreateBundle(
        string id,
        int level,
        double ram,
        double vram,
        int cpu,
        double disk) =>
        new()
        {
            Id = id,
            Level = level,
            Requirements = new ImageAnalysisHardwareRequirements
            {
                RamGb = ram,
                VramGb = vram,
                LogicalProcessorCount = cpu,
                FreeDiskGb = disk
            }
        };
}
