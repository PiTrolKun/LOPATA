using AIHub.Models;

namespace AIHub.Services.LivingArcade;

/// <summary>Host-owned finite effects. Models can choose a type, never executable mechanics.</summary>
public static class LivingArcadePowerups
{
    private static readonly string[] Positive = ["power", "rapid", "spread", "shield", "repair", "weapon", "cadence", "vitality", "time_scope",
        "weapon_pistol", "weapon_shotgun", "weapon_smg", "weapon_laser", "weapon_rocket"];
    private static readonly string[] Negative = ["jam", "drag", "fragile"];

    public static bool IsValidKind(LivingArcadeMiniEvent mini) => mini.BonusKind == "auto"
        || mini.Kind == "bonus" && (mini.BonusKind == "helper" || Positive.Contains(mini.BonusKind, StringComparer.Ordinal))
        || mini.Kind == "negative" && Negative.Contains(mini.BonusKind, StringComparer.Ordinal);

    internal static string Choose(int seed, int level, int ordinal, bool negative)
    {
        var bag = (negative ? Negative : Positive).ToArray();
        var cycle = ordinal / bag.Length;
        var state = unchecked((uint)seed ^ (uint)level * 0x9E3779B9u ^ (uint)cycle * 0x85EBCA6Bu);
        if (state == 0) state = 17;
        for (var i = bag.Length - 1; i > 0; i--)
        {
            state ^= state << 13; state ^= state >> 17; state ^= state << 5;
            var j = (int)(state % (i + 1));
            (bag[i], bag[j]) = (bag[j], bag[i]);
        }
        return bag[ordinal % bag.Length];
    }
}
