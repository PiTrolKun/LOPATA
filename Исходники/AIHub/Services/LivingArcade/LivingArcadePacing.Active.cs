using AIHub.Models;

namespace AIHub.Services.LivingArcade;

public static partial class LivingArcadePacing
{
    /// <summary>Finite host-owned attackers supplement model-planned elites and ordinary contact foes.</summary>
    private static LivingArcadeLevel AddActiveSupport(LivingArcadeLevel source)
    {
        const string activeId = "active_support";
        if (source.EnemyTypes.Count >= 12 || source.EnemyTypes.Any(e => e.Id == activeId)) return source;
        var support = source.EnemyTypes.FirstOrDefault(e => e.Id == "support" && IsCompanion(e))
            ?? source.EnemyTypes.FirstOrDefault(IsCompanion);
        if (support is null || !source.Waves.Any(w => w.EnemyId == support.Id)) return source;

        // Slower firing and sparser promotion are deterministic budget fallbacks. They never
        // multiply a model's existing recursive spawns or silently truncate its formations.
        foreach (var every in new[] { 3, 4, 6 })
        foreach (var interval in new[] { 6d, 8, 10, 12 })
        {
            var waves = new List<LivingArcadeWave>();
            var ordinal = 0;
            foreach (var wave in source.Waves.OrderBy(w => w.At))
            {
                if (wave.EnemyId != support.Id || ++ordinal % every != 0)
                { waves.Add(wave with { }); continue; }
                var attackers = Math.Min(2, Math.Max(1, wave.Count / 2));
                var remaining = wave.Count - attackers;
                if (remaining > 0) waves.Add(wave with { Count = remaining });
                waves.Add(wave with { EnemyId = activeId, Count = attackers,
                    X = Math.Clamp(wave.X + (ordinal % 2 == 0 ? .07 : -.07), .08, .92) });
            }
            if (!waves.Any(w => w.EnemyId == activeId)) return source;
            if (waves.Count > 120) continue;
            var raider = support with
            {
                Id = activeId, TitleRu = "Стрелок: " + support.TitleRu[..Math.Min(90, support.TitleRu.Length)],
                TitleEn = "Raider: " + support.TitleEn[..Math.Min(90, support.TitleEn.Length)],
                Health = Math.Min(12, support.Health), Color = "#FFB86C",
                States = support.States.Select(s => s with
                {
                    ShotInterval = interval, ShotCount = 1, ShotSpread = 0, ShotSpeed = .22,
                    ShotReturnAfterSeconds = 0, Shield = false, ShieldMode = "off", Telegraph = false
                }).ToList()
            };
            var adapted = source with { EnemyTypes = source.EnemyTypes.Select(e => e with { }).Append(raider).ToList(),
                Waves = waves.OrderBy(w => w.At).ToList() };
            LivingArcadeLevelPolicy.Validate(adapted);
            if (Fits(adapted)) return adapted;
        }
        return source;
    }
}
