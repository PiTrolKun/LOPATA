using System.IO;
using AIHub.Models;

namespace AIHub.Services.LivingArcade;

public sealed partial class LivingArcadeRunStore
{
    public bool TrySaveBossReward(string id, int level, LivingArcadeBossReward reward)
    {
        LivingArcadeRewardPolicy.Validate(reward);
        lock (_gate)
        {
            var run = Load(id);
            if (run is null || level != run.CurrentLevel || level % 3 != 0 || run.BossRewards.ContainsKey(level)) return false;
            run.BossRewards[level] = LivingArcadeRewardPolicy.Copy(reward);
            // Keep recent receipts without growing the manifest forever. Consumed rewards survive in hero state.
            run.BossRewards = run.BossRewards.Where(pair => pair.Key >= run.CurrentLevel - 9)
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            run.Revision++; Write(run); return true;
        }
    }

    public static LivingArcadeHeroState FinishActiveReward(LivingArcadeHeroState hero, int completedLevel)
    {
        LivingArcadeRewardPolicy.ValidateActive(hero.ActiveBossReward);
        if (hero.ActiveBossReward is not { } active) return hero with { };
        var remaining = active.RemainingLevels - (completedLevel >= active.StartLevel ? 1 : 0);
        return hero with { ActiveBossReward = remaining <= 0 ? null : active with
            { RemainingLevels = remaining, Reward = LivingArcadeRewardPolicy.Copy(active.Reward) } };
    }

    public static bool TryPrepareRewardedHeroForAdvance(LivingArcadeRun run, out LivingArcadeHeroState hero)
    {
        hero = run.PendingHero is { } pending ? pending with { } : run.EntryState with { };
        if (run.PendingHero is null) return false;
        if (run.PendingBossRewardLevel is not { } source) return true;
        if (source != run.CurrentLevel || !run.BossRewards.TryGetValue(source, out var reward)) return false;
        LivingArcadeRewardPolicy.Validate(reward);
        hero.ActiveBossReward = new() { Reward = LivingArcadeRewardPolicy.Copy(reward),
            RemainingLevels = reward.DurationLevels, StartLevel = checked(run.CurrentLevel + 1) };
        return true;
    }

    private static void ValidateRunRewards(LivingArcadeRun run)
    {
        if (run.BossRewards is null || run.BossRewards.Count > 8
            || run.BossRewards.Keys.Any(level => level < 3 || level % 3 != 0 || level > run.CurrentLevel)
            || run.PendingBossRewardLevel is { } source && (run.PendingHero is null || source != run.CurrentLevel || source % 3 != 0))
            throw new InvalidDataException("LivingArcade.InvalidSave");
        foreach (var reward in run.BossRewards.Values) LivingArcadeRewardPolicy.Validate(reward);
    }
}
