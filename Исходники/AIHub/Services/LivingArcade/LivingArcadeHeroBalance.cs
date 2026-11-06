using AIHub.Models;

namespace AIHub.Services.LivingArcade;

/// <summary>Versioned, bounded permanent progression; live powerups never enter saved hero stats.</summary>
public static class LivingArcadeHeroBalance
{
    public const int Version = 1;
    public const double MaximumWeaponPower = 1.35;
    public const double MaximumFireRate = 6;
    public const double MaximumPermanentPower = 1.25;
    public const double MaximumHealth = 120;

    public static LivingArcadeHeroState Copy(LivingArcadeHeroState source) => source with
    {
        Companions = source.Companions.Select(c => c with { }).ToList(),
        ActiveBossReward = source.ActiveBossReward is { } active ? active with
        { Reward = LivingArcadeRewardPolicy.Copy(active.Reward) } : null
    };

    public static (int Level, long KillsTowardNextLevel, long KillsRequiredForNextLevel) Progress(long totalKills)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalKills);
        var level = 1;
        var remaining = totalKills;
        long required = 10;
        while (remaining >= required)
        {
            remaining -= required;
            level++;
            required = required > long.MaxValue / 2 ? long.MaxValue : required * 2;
        }
        return (level, remaining, required);
    }

    public static LivingArcadeHeroState Normalize(LivingArcadeHeroState source)
    {
        // Old pickups simultaneously increased every stat. Retain a small part of that progress
        // once, without changing the original save or carrying its permanent projectile fan.
        var legacy = source.PowerupBalanceVersion == 0;
        return Copy(source) with
        {
            PowerupBalanceVersion = Version,
            WeaponPower = 1 + Math.Clamp((source.WeaponPower - 1) * (legacy ? .04 : 1), 0, MaximumWeaponPower - 1),
            FireRate = 5 + Math.Clamp((source.FireRate - 5) * (legacy ? .16 : 1), 0, MaximumFireRate - 5),
            PermanentPower = 1 + Math.Clamp((source.PermanentPower - 1) * (legacy ? .1 : 1), 0, MaximumPermanentPower - 1),
            MaxHealth = source.MaxHealth < 100 ? Math.Clamp(source.MaxHealth, 1, 100)
                : 100 + Math.Clamp((source.MaxHealth - 100) * (legacy ? .1 : 1), 0, MaximumHealth - 100),
            SpreadShots = 1
        };
    }
}
