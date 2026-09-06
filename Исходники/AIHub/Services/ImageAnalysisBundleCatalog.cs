using AIHub.Models;

namespace AIHub.Services;

public static class ImageAnalysisBundleCatalog
{
    public const string LightId = "light";
    public const string MediumId = "medium";
    public const string HeavyId = "heavy";

    public static IReadOnlyList<ImageAnalysisBundleDefinition> Create() =>
    [
        new ImageAnalysisBundleDefinition
        {
            Id = LightId, Level = 1, TitleKey = "ImageAnalysis.Bundle.Light",
            PurposeKey = "ImageAnalysis.Bundle.LightPurpose", StatusKey = "ImageAnalysis.Bundle.Experimental",
            Components = [new() { RoleKey = "ImageAnalysis.Role.Omni", ModelName = ManagedModelCatalog.OmniAlphaDisplayName, PlacementKey = "ImageAnalysis.Placement.Gpu" }],
            Requirements = new() { RamGb = 16, VramGb = 8, LogicalProcessorCount = 8, FreeDiskGb = 8 },
            IsAvailable = true, IsPreliminary = true
        },
        new ImageAnalysisBundleDefinition
        {
            Id = MediumId, Level = 2, TitleKey = "ImageAnalysis.Bundle.Medium",
            PurposeKey = "ImageAnalysis.Bundle.MediumPurpose", StatusKey = "ImageAnalysis.Bundle.Experimental",
            Components = [new() { RoleKey = "ImageAnalysis.Role.Omni", ModelName = ManagedModelCatalog.OmniBetaDisplayName, PlacementKey = "ImageAnalysis.Placement.Gpu" }],
            Requirements = new() { RamGb = 32, VramGb = 10, LogicalProcessorCount = 12, FreeDiskGb = 12 },
            IsAvailable = true, IsPreliminary = true, IsCurrentProjectBundle = true
        },
        CreateHeavyBundle()
    ];

    private static ImageAnalysisBundleDefinition CreateHeavyBundle() => new()
    {
        Id = HeavyId,
        Level = 3,
        TitleKey = "ImageAnalysis.Bundle.Heavy",
        PurposeKey = "ImageAnalysis.Bundle.HeavyPurpose",
        StatusKey = "ImageAnalysis.Bundle.Experimental",
        Components =
        [
            new ImageAnalysisBundleComponent
            {
                RoleKey = "ImageAnalysis.Role.Omni",
                ModelName = "Qwen2.5-Omni-3B BF16 · Thinker",
                PlacementKey = "ImageAnalysis.Placement.Gpu"
            }
        ],
        Requirements = new ImageAnalysisHardwareRequirements
        {
            RamGb = 32,
            VramGb = 24,
            LogicalProcessorCount = 12,
            FreeDiskGb = 16
        },
        IsAvailable = true,
        IsCurrentProjectBundle = false,
        IsPreliminary = true
    };

}
