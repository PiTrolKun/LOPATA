using AIHub.Models;

namespace AIHub.Services.LivingArcade;

/// <summary>Chronological coverage and bounded crowd growth, independent of sprite detail.</summary>
public static partial class LivingArcadePacing
{
    internal sealed record Band(bool Lead, int FirstSecond, int LastSecond, int MinimumCount, int MaximumCount);

    internal static bool HasContinuousSchedule(LivingArcadeLevel level)
    {
        var expected=28+Math.Clamp(level.Number-2,0,10)*2;
        var times=level.Waves.SelectMany(w=>Enumerable.Range(0,w.Count).Select(i=>w.At+i*w.SpawnInterval)).Order().ToArray();
        return level.Waves.Count==expected && times.Length>=expected*2 && times[0]<=7 && times[^1]>=112
            && times.Zip(times.Skip(1),(a,b)=>b-a).All(gap=>gap<=9);
    }

    internal static IReadOnlyList<Band> Bands(int number, double leadShotRate, int splitCount, bool rareLarge)
    {
        var count = 28 + Math.Clamp(number - 2, 0, 10) * 2;
        var width = 114d / count;
        // All leaf companions can survive 35s; descendants of every lead plus all ten
        // possible ambushes are reserved separately. The existing 80/480 caps stay intact.
        var concurrentBands = (int)Math.Ceiling(35 / width) + 2;
        // Reserve space for leaf formations and their ten possible ambushes before increasing
        // special enemies. Some firing budget remains for the host's light raider formations.
        var unitLimit = Math.Max(1, (80 - 10 - concurrentBands * 3) / (1 + splitCount));
        var leadCount = rareLarge ? 1 : Math.Min(Math.Min(10, unitLimit),
            leadShotRate == 0 ? 10 : Math.Max(1, (int)Math.Floor(30 / leadShotRate)));
        var maximum = Math.Clamp((80 - leadCount * (1 + splitCount) - 10) / concurrentBands, 3, 6);
        var leaders = Enumerable.Range(0, leadCount).Select(i => (int)Math.Floor((i + .5) * count / leadCount)).ToHashSet();
        return Enumerable.Range(0, count).Select(i => new Band(leaders.Contains(i),
            (int)Math.Floor(2 + i * width), Math.Min(116, (int)Math.Ceiling(2 + (i + 1) * width) - 1),
            leaders.Contains(i) ? 1 : 3, leaders.Contains(i) ? 1 : maximum)).ToArray();
    }

    /// <summary>Repairs sparse already-saved generated levels in memory, without rewriting their source files.</summary>
    public static LivingArcadeLevel ForPlay(LivingArcadeLevel source)
    {
        LivingArcadeLevelPolicy.Validate(source);
        if (source.Authored || source.Number < 2)
            return source;
        if (source.DurationSeconds != 120 || source.Waves.Count > 12) return AddActiveSupport(source);
        var support = source.EnemyTypes.FirstOrDefault(e => e.Id == "support" && IsCompanion(e))
            ?? source.EnemyTypes.FirstOrDefault(IsCompanion);
        if (support is null) return AddActiveSupport(SpreadExisting(source));
        var lead = source.EnemyTypes.FirstOrDefault(e => e.Id == "lead") ?? source.EnemyTypes[0];
        var rate = lead.States.Max(s => s.ShotInterval == 0 ? 0 : s.ShotCount / s.ShotInterval);
        var bands = Bands(source.Number, rate, lead.DeathSpawnCount, lead.EncounterClass == "rare-large");
        var random = new StableRandom(unchecked((uint)source.Seed ^ (uint)source.Number * 0x9E3779B9u));
        var waves = bands.Select(band =>
        {
            var type = band.Lead ? lead : support;
            var templates = source.Waves.Where(w => w.EnemyId == type.Id).ToArray();
            var template = templates.Length == 0 ? source.Waves[0] : templates[random.Index(templates.Length)];
            return template with {
                At = band.FirstSecond + random.Index(band.LastSecond - band.FirstSecond + 1), EnemyId = type.Id,
                Count = band.MinimumCount + random.Index(band.MaximumCount - band.MinimumCount + 1),
                X = type.EncounterClass == "rare-large" ? .25 + random.Next() * .5 : .12 + random.Next() * .76,
                Spacing = .045 + random.Next() * .08, SpawnInterval = random.Index(3) * .25
            };
        }).ToList();
        var upgraded = source with { Waves = waves, EnemyTypes = source.EnemyTypes.Select(e => e.Id != support.Id ? e : e with {
            Speed = Math.Max(.06, e.Speed), States = e.States.Select(s => s with { ForwardSpeed = Math.Max(.75, s.ForwardSpeed) }).ToList()
        }).ToList() };
        LivingArcadeLevelPolicy.Validate(upgraded);
        return AddActiveSupport(Fits(upgraded) ? upgraded : SpreadExisting(source));
    }

    private static bool IsCompanion(LivingArcadeEnemyDefinition enemy) => enemy.EncounterClass == "normal"
        && enemy.DeathSpawnCount == 0 && enemy.States.All(s => s.ShotInterval == 0 && s.Motion is not ("hold" or "dash"));

    private static LivingArcadeLevel SpreadExisting(LivingArcadeLevel source)
    {
        // Older models may have supplied only shooting enemies: retime their existing
        // formations instead of adding unbudgeted shooters or inventing a new enemy.
        var ordered = source.Waves.OrderBy(w => w.At).ToArray();
        var waves = ordered.Select((wave, i) => wave with {
            At = ordered.Length == 1 ? 2 : 2 + 114d * i / (ordered.Length - 1),
            SpawnInterval = Math.Min(wave.SpawnInterval, Math.Min(.5, 3d / Math.Max(1,wave.Count-1)))
        }).ToList();
        var retimed = source with { Waves = waves };
        return Fits(retimed) ? retimed : source;
    }

    private static bool Fits(LivingArcadeLevel level)
    {
        var cost = LivingArcadeLevelPolicy.EstimateCost(level);
        return cost.ExpandedSpawnCount <= 1200 && cost.PeakScheduledEnemies <= 80 && cost.EstimatedPeakHostileShots <= 480;
    }

    private sealed class StableRandom(uint seed)
    {
        private uint _state = seed == 0 ? 0x9E3779B9u : seed;
        public double Next()
        {
            _state ^= _state << 13; _state ^= _state >> 17; _state ^= _state << 5;
            return _state / ((double)uint.MaxValue + 1);
        }
        public int Index(int count) => (int)(Next() * count);
    }
}
