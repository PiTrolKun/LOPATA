using AIHub.Models;

namespace AIHub.Services.LivingArcade;

public sealed partial class LivingArcadeRunStore
{
    /// <summary>Persists only experience, the ordinary weapon and each companion's remaining life.</summary>
    public void RecordCombatProgress(string id, LivingArcadeHeroState hero)
    {
        lock (_gate)
        {
            var run = Load(id);
            if (run is null || run.PendingHero is not null) return;
            ValidateHero(hero);
            var merged = PreserveCombatProgress(run.EntryState, hero);
            if (SameCombatProgress(run.EntryState, merged)) return;
            run.EntryState = merged;
            run.Revision++;
            Write(run);
        }
    }

    private static LivingArcadeHeroState PreserveCombatProgress(LivingArcadeHeroState stored, LivingArcadeHeroState incoming)
    {
        var baseline = LivingArcadeHeroBalance.Normalize(stored);
        // A checkpoint from an older combat snapshot cannot undo a later weapon or companion death.
        if (incoming.TotalKills < stored.TotalKills) return baseline;
        return baseline with
        {
            TotalKills = Math.Max(stored.TotalKills, incoming.TotalKills),
            WeaponKind = incoming.WeaponKind,
            Companions = incoming.Companions.Where(c => c.Health > 0).Select(c => c with { }).ToList()
        };
    }

    private static bool SameCombatProgress(LivingArcadeHeroState left, LivingArcadeHeroState right) =>
        left.TotalKills == right.TotalKills && left.WeaponKind == right.WeaponKind
        && left.Companions.SequenceEqual(right.Companions);
}
