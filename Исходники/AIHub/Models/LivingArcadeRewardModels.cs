using System.Text.Json.Serialization;

namespace AIHub.Models;

/// <summary>A model-authored combination of host mechanics, never executable code.</summary>
public sealed record LivingArcadeBossReward
{
    [JsonRequired] public string TitleRu { get; set; } = "";
    [JsonRequired] public string TitleEn { get; set; } = "";
    [JsonRequired] public string DescriptionRu { get; set; } = "";
    [JsonRequired] public string DescriptionEn { get; set; } = "";
    [JsonRequired] public int DurationLevels { get; set; } = 1;
    [JsonRequired] public double DamageMultiplier { get; set; } = 1;
    [JsonRequired] public double FireRateMultiplier { get; set; } = 1;
    [JsonRequired] public string ShotPattern { get; set; } = "straight";
    [JsonRequired] public int Pierce { get; set; }
    [JsonRequired] public string ColorHex { get; set; } = "#FFD166";
    public List<LivingArcadeModelCredit> Credits { get; set; } = [];
    public DateTimeOffset GeneratedUtc { get; set; }
}

public sealed record LivingArcadeActiveReward
{
    public LivingArcadeBossReward Reward { get; set; } = new();
    public int RemainingLevels { get; set; }
    public int StartLevel { get; set; }
}
