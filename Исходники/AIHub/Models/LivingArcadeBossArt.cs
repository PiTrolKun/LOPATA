namespace AIHub.Models;

/// <summary>Optional boss appearance; health, size, movement and phase rules remain runtime-owned.</summary>
public sealed class LivingArcadeBossArt
{
    public string TitleRu { get; set; } = "";
    public string TitleEn { get; set; } = "";
    public LivingArcadeVisual Visual { get; set; } = new();
    public List<LivingArcadeModelCredit> Credits { get; set; } = [];
    public List<LivingArcadeAssetCredit> AssetCredits { get; set; } = [];
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public string Source { get; set; } = "model";
    // Host facts, excluded from the artist response schema and reset before processing model output.
    public bool GeometryFitted { get; set; }
    public bool PaletteDeduplicated { get; set; }
    public bool InactiveAnimationDefaulted { get; set; }
}
