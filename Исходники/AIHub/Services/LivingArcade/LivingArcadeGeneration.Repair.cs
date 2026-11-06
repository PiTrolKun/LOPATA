using AIHub.Models;

namespace AIHub.Services.LivingArcade;

public sealed partial class LivingArcadeGeneration
{
    // Diagnostics only. The authoritative validator remains LivingArcadeLevelPolicy.Validate.
    private static string LevelRepairHint(LivingArcadeLevel level)
    {
        static bool Range(double n, double lo, double hi) => double.IsFinite(n) && n >= lo && n <= hi;
        if (level.SchemaVersion != 1) return "schemaVersion must be1.";
        if (!Range(level.DurationSeconds, 30, 300)) return "durationSeconds must be30..300.";
        if (!Range(level.CarryFraction, 0, 1)) return "carryFraction must be0..1.";
        if (level.EnemyTypes is null || level.EnemyTypes.Count is < 1 or > 12) return "enemyTypes must contain1..12 objects.";
        var ids = level.EnemyTypes.Where(e => e is not null).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        if (ids.Count != level.EnemyTypes.Count) return "enemyTypes contain null or duplicate IDs.";
        foreach (var enemy in level.EnemyTypes)
        {
            if (enemy is null) return "An enemyTypes element is null.";
            if (!Range(enemy.Health, .25, 200) || !Range(enemy.Speed, .015, .2) || !Range(enemy.Radius, .012, enemy.EncounterClass=="rare-large"?.20:.07))
                return $"Enemy {enemy.Id}: health.25..200,speed.015..0.2,normal radius.012..0.07;rare-large up to.20.";
            if (enemy.States is null || enemy.States.Count is < 1 or > 8) return $"Enemy {enemy.Id}: states need1..8 objects.";
            var states = enemy.States.Where(s => s is not null).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            if (states.Count != enemy.States.Count || !states.Contains(enemy.InitialState))
                return $"Enemy {enemy.Id}: state IDs must be unique; initialState must exist.";
            foreach (var state in enemy.States)
            {
                if (state is null) return $"Enemy {enemy.Id}: null state.";
                if (!states.Contains(state.NextState)) return $"Enemy {enemy.Id},state {state.Id}: nextState {state.NextState} does not exist in this enemy.";
                if (!Range(state.Duration, .2, 20) || !Range(state.ForwardSpeed, 0, 3) || !Range(state.HorizontalSpeed, -.5, .5))
                    return $"Enemy {enemy.Id},state {state.Id}: duration.2..20,forwardSpeed0..3,horizontalSpeed-.5..+.5.";
                if (!Range(state.ShotInterval, 0, 20) || state.ShotInterval is > 0 and < .4 || state.ShotCount is < 1 or > 8
                    || !Range(state.ShotSpread, 0, 100) || !Range(state.ShotSpeed, .1, .7))
                    return $"Enemy {enemy.Id},state {state.Id}: shotInterval0 or.4..20,shotCount1..8,shotSpread0..100,shotSpeed.1..0.7.";
            }
            if (enemy.DeathSpawnCount > 0 && (enemy.DeathSpawnId is null || !ids.Contains(enemy.DeathSpawnId)))
                return $"Enemy {enemy.Id}: deathSpawnId must name an existing enemy.";
            if (enemy.States.All(s => LivingArcadeLevelPolicy.ShieldMode(s)=="full")) return $"Enemy {enemy.Id}: permanently shielded; add an open state in the reachable repeating cycle.";
            var route = new List<LivingArcadeEnemyState>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var next = enemy.InitialState;
            while (visited.Add(next))
            { var current = enemy.States.Single(s => s.Id == next); route.Add(current); next = current.NextState; }
            if (route.SkipWhile(s => s.Id != next).All(s => LivingArcadeLevelPolicy.ShieldMode(s)=="full"))
                return $"Enemy {enemy.Id}: the reachable repeating cycle is permanently shielded. Link it back to an open state; an unreachable or one-time open state is insufficient.";
        }
        if (level.Waves is null || level.Waves.Count is < 1 or > 120) return "waves require1..120 objects.";
        if (level.Waves.Sum(w => w?.Count ?? 1000) > 400) return "Total wave enemy count exceeds400.";
        foreach (var wave in level.Waves)
        {
            if (wave is null || !ids.Contains(wave.EnemyId)) return "A wave is null or names an unknown enemyId.";
            if (!Range(wave.At, 0, level.DurationSeconds) || wave.At + (wave.Count - 1) * wave.SpawnInterval > level.DurationSeconds)
                return $"Wave at{wave.At}: final spawn(at+(count-1)*spawnInterval) must not exceed durationSeconds.";
            if (wave.Count is < 1 or > 12 || !Range(wave.X, .08, .92) || !Range(wave.Spacing, .035, .25)
                || !Range(wave.SpawnInterval, 0, 5)) return $"Wave at{wave.At}: count1..12,x.08..0.92,spacing.035..0.25,spawnInterval0..5.";
        }
        return "Check color #RRGGBB; supported shapes/motions; all IDs and finite ranges; no recursive death splitting; "
            + "every reachable repeating state cycle needs an unshielded state; mini-events use known enemy IDs; MIDI notes36..96.";
    }
}
