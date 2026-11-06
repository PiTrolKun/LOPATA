using System.Globalization;
using AIHub.Models;

namespace AIHub.Controls;

internal static class LivingArcadeHud
{
    internal static string KillCount(long count) => count.ToString("N0", CultureInfo.InvariantCulture).Replace(',', ' ');

    internal static string TimedEffect(Func<string, string> localize, string kind, double seconds) =>
        Name(localize, kind) + $" {Math.Ceiling(seconds):0} {localize("Arcade.SecondsShort")}";

    internal static string Pickup(Func<string, string> localize, LivingArcadeBonusFeedback bonus)
    {
        var name = Name(localize, bonus.Kind);
        var prefix = localize(bonus.IsNegative ? "Arcade.Bonus.Negative" : "Arcade.Bonus.Picked");
        if (bonus.IsNegative) return prefix + " " + TimedEffect(localize, bonus.Kind, bonus.DurationSeconds);
        var changes = new List<string>();
        if (bonus.DamageGain > 0) changes.Add(string.Format(localize("Arcade.Bonus.Damage"), bonus.DamageGain));
        if (bonus.FireRateGain > 0) changes.Add(string.Format(localize("Arcade.Bonus.FireRate"), bonus.FireRateGain));
        if (bonus.ExtraShots > 0) changes.Add(string.Format(localize("Arcade.Bonus.Shots"), bonus.ExtraShots));
        if (bonus.HealthRestored > 0) changes.Add(string.Format(localize("Arcade.Bonus.Health"), bonus.HealthRestored));
        if (bonus.MaxHealthGain > 0) changes.Add(string.Format(localize("Arcade.Bonus.MaxHealth"), bonus.MaxHealthGain));
        if (bonus.TimeScopeChanged) changes.Add(localize("Arcade.Bonus.TimeScope"));
        if (bonus.DurationSeconds > 0) changes.Add($"{bonus.DurationSeconds:0} {localize("Arcade.SecondsShort")}");
        if (bonus.Kind is "weapon" or "cadence" or "vitality") changes.Add(localize("Arcade.Bonus.Permanent"));
        if (bonus.Kind is "weapon_pistol" or "weapon_shotgun" or "weapon_smg") changes.Add(localize("Arcade.Weapon.UntilReplaced"));
        if (bonus.Kind == "helper") changes.Add(localize(bonus.CompanionAdded ? "Arcade.Companion.OneLife" : "Arcade.Bonus.Maximum"));
        return prefix + " " + (name.Length > 0 ? name + ": " : "")
            + (changes.Count > 0 ? string.Join(", ", changes) : localize("Arcade.Bonus.Maximum"));
    }

    internal static string Weapon(Func<string, string> localize, string kind, double seconds)
    {
        var known = kind is "pistol" or "shotgun" or "smg" or "laser" or "rockets" ? kind : "pistol";
        var text = string.Format(localize("Arcade.Weapon.Current"), localize("Arcade.Weapon." + known));
        return seconds > 0 ? text + $" {Math.Ceiling(seconds):0} {localize("Arcade.SecondsShort")}" : text;
    }

    internal static string Companions(Func<string, string> localize, IReadOnlyList<LivingArcadeEntityView> companions)
    {
        var living = companions.Where(c => c.HealthFraction > 0).ToArray();
        if (living.Length == 0) return "";
        var health = string.Join(", ", living.Select(c => c.MaxHealth > 0
            ? $"{Math.Ceiling(c.Health):0}/{Math.Ceiling(c.MaxHealth):0}"
            : $"{Math.Ceiling(c.HealthFraction * 100):0}%"));
        return string.Format(localize("Arcade.Companion.Health"), living.Length, health);
    }

    internal static string BossReward(Func<string, string> localize, LivingArcadeActiveReward active, string language) =>
        string.Format(localize("Arcade.BossReward.Active"), language.StartsWith("en", StringComparison.OrdinalIgnoreCase)
            ? active.Reward.TitleEn : active.Reward.TitleRu, active.RemainingLevels);

    internal static string BossRewardDetails(Func<string, string> localize, LivingArcadeBossReward reward)
    {
        // The model's prose is retained separately; only executable fields describe actual effects here.
        var pattern = reward.ShotPattern is "fan" or "wave" or "returning" ? reward.ShotPattern : "straight";
        return string.Join(Environment.NewLine,
            string.Format(localize("Arcade.BossReward.Damage"), Math.Round((reward.DamageMultiplier - 1) * 100, 2)),
            string.Format(localize("Arcade.BossReward.FireRate"), Math.Round((reward.FireRateMultiplier - 1) * 100, 2)),
            localize("Arcade.BossReward.Pattern." + pattern),
            reward.Pierce > 0 ? string.Format(localize("Arcade.BossReward.Pierce"), reward.Pierce)
                : localize("Arcade.BossReward.NoPierce"),
            string.Format(localize("Arcade.BossReward.Duration"), reward.DurationLevels));
    }

    private static string Name(Func<string, string> localize, string kind) => kind switch
    {
        "power" or "rapid" or "spread" or "shield" or "repair" or "weapon" or "cadence" or "vitality" or "time_scope"
            or "jam" or "drag" or "fragile" or "weapon_pistol" or "weapon_shotgun" or "weapon_smg"
            or "weapon_laser" or "weapon_rocket" or "helper" => localize("Arcade.Powerup." + kind),
        _ => ""
    };
}
