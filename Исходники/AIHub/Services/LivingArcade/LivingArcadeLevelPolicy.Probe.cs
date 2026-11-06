using System.Diagnostics;
using AIHub.Models;

namespace AIHub.Services.LivingArcade;

public static partial class LivingArcadeLevelPolicy
{
    /// <summary>
    /// Rejects workload that would be silently discarded by simulation caps, then runs three
    /// deterministic inputs. A bot's defeat is evidence for the critic, never a rejection.
    /// CPU time and tick budgets cap validation itself; truncation is explicitly reported.
    /// </summary>
    public static LivingArcadeProbeResult Probe(LivingArcadeLevel level, LivingArcadeHeroState? hero = null,
        CancellationToken cancellationToken = default)
    {
        Validate(level);
        var clock = Stopwatch.StartNew();
        var (expanded, peak, peakShots) = EstimateValidatedCost(level);
        var rejection = expanded > 1200 || peak > 80 || peakShots > 480
            ? "LivingArcade.LevelCostExceeded" : null;
        var trajectories = new List<LivingArcadeProbeTrajectory>();
        var ticks = 0; var truncated = false;
        if (rejection is null)
        {
            for (var path = 0; path < 3; path++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var simulation = new LivingArcadeSimulation(level, hero ?? new());
                var peakEnemies = 0; var maxShots = 0; var frames = 0;
                LivingArcadeSnapshot snapshot = simulation.Snapshot;
                var timeBound = level.DurationSeconds + 176;
                while (!snapshot.IsComplete && !snapshot.IsDefeated && snapshot.Progress <= timeBound)
                {
                    if (++frames > 2000 || ticks >= 90_000 || clock.ElapsedMilliseconds >= 1500)
                    { truncated = true; break; }
                    if ((frames & 31) == 0) cancellationToken.ThrowIfCancellationRequested();
                    var move = path switch
                    {
                        1 => Math.Sin(snapshot.Progress * .4) > 0 ? 1 : -1,
                        2 => Math.Sin(snapshot.Progress * 1.1) > 0 ? 1 : -1,
                        _ => 0
                    };
                    simulation.Advance(.25, new(Left: move < 0, Right: move > 0)); ticks += 15;
                    snapshot = simulation.Snapshot;
                    peakEnemies = Math.Max(peakEnemies, snapshot.Enemies.Count);
                    maxShots = Math.Max(maxShots, snapshot.Shots.Count);
                    if (!Finite(snapshot) || snapshot.Enemies.Count > 80 || snapshot.Shots.Count > 512)
                    { rejection = "LivingArcade.LevelProbeFailed"; break; }
                }
                var reachedBound = !snapshot.IsComplete && !snapshot.IsDefeated && !truncated;
                if (reachedBound) rejection = "LivingArcade.LevelProbeFailed";
                trajectories.Add(new(path switch { 0 => "center", 1 => "sweep", _ => "zigzag" },
                    snapshot.Progress, snapshot.DamageTaken, snapshot.IsComplete, snapshot.IsDefeated,
                    reachedBound, peakEnemies, maxShots));
                if (truncated || rejection is not null) break;
            }
        }
        return new(rejection is null, rejection, expanded, peak, peakShots, truncated, ticks,
            clock.Elapsed.TotalMilliseconds, trajectories);
    }

    /// <summary>Uses the same conservative workload estimate as Probe, without running simulated play.</summary>
    internal static (int ExpandedSpawnCount, int PeakScheduledEnemies, double EstimatedPeakHostileShots)
        EstimateCost(LivingArcadeLevel level)
    {
        Validate(level);
        return EstimateValidatedCost(level);
    }

    private static (int ExpandedSpawnCount, int PeakScheduledEnemies, double EstimatedPeakHostileShots)
        EstimateValidatedCost(LivingArcadeLevel level)
    {
        var types = level.EnemyTypes.ToDictionary(e => e.Id, StringComparer.Ordinal);
        (int Count, double Shots) Expansion(string id)
        {
            var enemy = types[id];
            var reachable = new HashSet<string>(StringComparer.Ordinal);
            var next = enemy.InitialState;
            double rate = 0;
            while (reachable.Add(next))
            {
                var state = enemy.States.Single(s => s.Id == next);
                rate = Math.Max(rate, state.ShotInterval == 0 ? 0 : state.ShotCount / state.ShotInterval);
                next = state.NextState;
            }
            if (enemy.DeathSpawnCount == 0) return (1, rate);
            var child = Expansion(enemy.DeathSpawnId!);
            return (1 + enemy.DeathSpawnCount * child.Count, rate + enemy.DeathSpawnCount * child.Shots);
        }
        var windows = new List<(double At, int Units, double Shots)>();
        var expanded = 0;
        foreach (var wave in level.Waves)
        {
            var cost = Expansion(wave.EnemyId);
            for (var i = 0; i < wave.Count; i++)
            {
                var start = wave.At + i * wave.SpawnInterval;
                windows.Add((start, cost.Count, cost.Shots));
                // Descendants can be spawned late; use a conservative 175-second envelope.
                windows.Add((start + (cost.Count == 1 ? 35 : 175), -cost.Count, -cost.Shots));
                expanded += cost.Count;
            }
        }
        foreach (var mini in level.MiniEvents.Where(m => m.Kind == "ambush"))
        {
            var cost = Expansion(mini.EnemyId!);
            windows.Add((mini.At, cost.Count, cost.Shots));
            windows.Add((mini.At + (cost.Count == 1 ? 35 : 175), -cost.Count, -cost.Shots));
            expanded += cost.Count;
        }
        foreach (var mini in level.MiniEvents.Where(m => m.Kind == "meteor"))
        {
            windows.Add((mini.At, 0, 1d / 12));
            windows.Add((mini.At + 6, 0, -1d / 12));
        }
        var active = 0; var peak = 0; double activeShots = 0, peakShots = 0;
        foreach (var window in windows.OrderBy(w => w.At).ThenBy(w => w.Units))
        {
            active += window.Units; activeShots += window.Shots;
            peak = Math.Max(peak, active);
            // Returning projectiles share this explicit 12-second lifetime ceiling; turning
            // or homing never multiplies the volley count. Art detail is budgeted separately.
            peakShots = Math.Max(peakShots, activeShots * 12);
        }
        if (level.Number % 3 == 0)
        {
            // The authoritative boss starts only after all ordinary enemies have gone and
            // clears their remaining projectiles. Its peak is separate, rather than additive.
            expanded++;
            peak = Math.Max(peak, 1);
            peakShots = Math.Max(peakShots, Math.Ceiling(7 / .95 * 12));
        }
        return (expanded, peak, peakShots);
    }

    private static bool Finite(LivingArcadeSnapshot snapshot) =>
        double.IsFinite(snapshot.PlayerX) && double.IsFinite(snapshot.Health) && double.IsFinite(snapshot.Progress)
        && double.IsFinite(snapshot.DamageTaken) && snapshot.Enemies.Concat(snapshot.Shots).Concat(snapshot.Pickups)
            .All(e => double.IsFinite(e.X) && double.IsFinite(e.Y) && double.IsFinite(e.Radius) && double.IsFinite(e.HealthFraction));
}
