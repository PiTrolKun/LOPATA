using AIHub.Models;

namespace AIHub.Services.LivingArcade;

public sealed partial class LivingArcadeGeneration
{
    internal const int BalanceProfileVersion = 1;
    internal const string BalanceInstruction = """
        Balance for the supplied BASELINE hero, WITHOUT any random boss reward. Boss rewards are temporary advantages,
        never a reason to raise enemy health, incoming damage, firing pressure, crowd density or boss requirements.
        Recent results can reflect lucky bonuses: low damage taken alone does not prove greater baseline strength.
        The measured probe uses the playable copy with host support shooters/pickups and no boss reward.
        Treat bot defeats as balance evidence for your judgement, not an automatic rejection or proof of impossibility.
        """;

    internal static LivingArcadeHeroState BaselineHero(LivingArcadeHeroState hero) =>
        LivingArcadeHeroBalance.Normalize(hero) with { ActiveBossReward = null };

    // Exclude the reward property completely from model requests, not only its multiplier.
    private static object ModelBaselineHero(LivingArcadeHeroState source)
    {
        var hero = BaselineHero(source);
        return new { hero.PowerupBalanceVersion, hero.TotalKills, hero.PermanentPower, hero.WeaponPower,
            hero.MaxHealth, hero.FireRate, hero.SpreadShots, hero.WeaponKind, hero.Companions };
    }

    internal static LivingArcadeLevel PlayableCandidate(LivingArcadeLevel candidate) =>
        LivingArcadePickupSchedule.ForPlay(LivingArcadePacing.ForPlay(candidate));

    internal static LivingArcadeProbeResult ProbeBaselineCandidate(LivingArcadeLevel candidate,
        LivingArcadeHeroState hero, CancellationToken cancellationToken = default) =>
        LivingArcadeLevelPolicy.Probe(PlayableCandidate(candidate), BaselineHero(hero), cancellationToken);

    private static void ResetLegacyBalanceCheckpoint(GenerationCheckpoint checkpoint)
    {
        if (checkpoint.BalanceProfile == BalanceProfileVersion) return;
        // Only unfinished ordinary level work belongs to this checkpoint. Ready run.Levels
        // stay authoritative, and a confirmed boss reward must not be redrawn or discarded.
        checkpoint.InputContext = checkpoint.DraftContext = null;
        checkpoint.Design = checkpoint.Draft = null;
        checkpoint.Candidate = checkpoint.Level = null;
        checkpoint.Probe = null; checkpoint.Art = null;
        checkpoint.ReviewReason = checkpoint.Rejection = null;
        checkpoint.ProgramAttempt = 0; checkpoint.Attempts = [];
        checkpoint.BalanceProfile = BalanceProfileVersion;
    }
}
