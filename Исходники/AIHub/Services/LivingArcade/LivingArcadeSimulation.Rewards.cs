using AIHub.Models;

namespace AIHub.Services.LivingArcade;

public sealed partial class LivingArcadeSimulation
{
    private LivingArcadeActiveReward? ActiveReward => _hero.ActiveBossReward is { RemainingLevels: > 0 } active
        && _level.Number >= active.StartLevel ? active : null;
    private double RewardDamageMultiplier => ActiveReward?.Reward.DamageMultiplier ?? 1;
    private double RewardFireRateMultiplier => ActiveReward?.Reward.FireRateMultiplier ?? 1;
    private string RewardPattern => ActiveReward?.Reward.ShotPattern ?? "straight";

    partial void ConfigurePlayerShot(Shot shot)
    {
        if (ActiveReward is not { } active) return;
        shot.Color = active.Reward.ColorHex;
        shot.Pierces = Math.Max(shot.Pierces, active.Reward.Pierce);
        if (active.Reward.ShotPattern == "wave") shot.Wave = true;
        if (active.Reward.ShotPattern == "returning") shot.ReturnAfter = .88 / shot.Speed;
    }
}
