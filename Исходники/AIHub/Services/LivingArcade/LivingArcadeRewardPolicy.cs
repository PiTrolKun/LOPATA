using System.IO;
using AIHub.Models;

namespace AIHub.Services.LivingArcade;

public static class LivingArcadeRewardPolicy
{
    public static void Validate(LivingArcadeBossReward reward, bool requireModelCredit = true)
    {
        if (reward is null || !Text(reward.TitleRu, 80) || !Text(reward.TitleEn, 80)
            || !Text(reward.DescriptionRu, 500) || !Text(reward.DescriptionEn, 500)
            || reward.DurationLevels is < 1 or > 2
            || !Range(reward.DamageMultiplier, 1, 2) || !Range(reward.FireRateMultiplier, 1, 1.5)
            || reward.ShotPattern is not ("straight" or "fan" or "wave" or "returning")
            || reward.Pierce is < 0 or > 2 || reward.ColorHex is not { Length: 7 }
            || reward.ColorHex[0] != '#' || !reward.ColorHex.Skip(1).All(Uri.IsHexDigit)
            || reward.Credits is null || reward.Credits.Count > 4
            || reward.DamageMultiplier < 1.35 && reward.FireRateMultiplier < 1.25
                && reward.ShotPattern != "fan" && reward.Pierce == 0)
            throw Invalid();
        var budget = reward.DamageMultiplier * reward.FireRateMultiplier
            * (reward.ShotPattern == "fan" ? 1.6 : 1) * (1 + reward.Pierce * .25);
        if (budget > 4) throw Invalid();
        if (requireModelCredit && (reward.GeneratedUtc == default || reward.Credits.Count != 1
            || reward.Credits[0] is not { Role: "boss_reward", ModelId: "Qwen/Qwen2.5-Coder-7B-Instruct-GGUF" }
            || reward.Credits[0].Revision != LivingArcadeModels.MainRevision)) throw Invalid();
    }

    public static void ValidateActive(LivingArcadeActiveReward? active)
    {
        if (active is null) return;
        Validate(active.Reward);
        if (active.RemainingLevels is < 1 or > 2 || active.RemainingLevels > active.Reward.DurationLevels
            || active.StartLevel < 2) throw Invalid();
    }

    internal static LivingArcadeBossReward Copy(LivingArcadeBossReward reward) => reward with
    { Credits = reward.Credits.Select(c => c with { }).ToList() };

    private static bool Text(string? text, int limit) => !string.IsNullOrWhiteSpace(text)
        && text.Length <= limit && !text.Any(char.IsControl);
    private static bool Range(double value, double low, double high) => double.IsFinite(value) && value >= low && value <= high;
    private static InvalidDataException Invalid() => new("LivingArcade.InvalidBossReward");
}
