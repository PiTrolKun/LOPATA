using System.IO;
using AIHub.Models;

namespace AIHub.Services.LivingArcade;

public static class LivingArcadeCombatRules
{
    public static bool IsActive(LivingArcadeEnemyDefinition definition)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var stateId = definition.InitialState;
        while (visited.Add(stateId))
        {
            var state = definition.States.Single(s => s.Id == stateId);
            if (state.ShotInterval > 0) return true;
            stateId = state.NextState;
        }
        return false;
    }

    public static int ExperienceValue(LivingArcadeEnemyDefinition definition, bool boss = false) => boss ? 30 : IsActive(definition) ? 5 : 1;
    public static double HealthMultiplier(LivingArcadeEnemyDefinition definition) => IsActive(definition) ? .9 : 1;

    public static void ValidateHeroCombat(LivingArcadeHeroState hero)
    {
        if (hero.WeaponKind is not ("pistol" or "shotgun" or "smg") || hero.Companions is null || hero.Companions.Count > 2
            || hero.Companions.Any(c => c is null || string.IsNullOrEmpty(c.Id) || c.Id.Length > 96
                || !c.Id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-')
                || c.Kind is not ("drone" or "gunner") || !double.IsFinite(c.Health) || !double.IsFinite(c.MaxHealth)
                || c.MaxHealth is < 1 or > 100 || c.Health < 0 || c.Health > c.MaxHealth)
            || hero.Companions.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != hero.Companions.Count)
            throw new InvalidDataException("LivingArcade.InvalidSave");
        LivingArcadeRewardPolicy.ValidateActive(hero.ActiveBossReward);
    }
}
