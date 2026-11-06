using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services.LivingArcade;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class LivingArcadeCombatEvidenceTests
{
    [TestMethod, TestCategory("LocalEvidence")]
    public void ARealSavedLevelRunsThroughTheAdapterWithoutChangingItsManifest()
    {
        var source = Environment.GetEnvironmentVariable("LOPATA_ARCADE_COMBAT_MANIFEST");
        var output = Environment.GetEnvironmentVariable("LOPATA_ARCADE_COMBAT_EVIDENCE_DIR");
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(output))
        { Assert.Inconclusive("Explicit read-only manifest and isolated evidence directory are required."); return; }
        source = Path.GetFullPath(source); output = Path.GetFullPath(output);
        var saveDirectory = Path.GetDirectoryName(source)!.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Assert.IsFalse((output + Path.DirectorySeparatorChar).StartsWith(saveDirectory, StringComparison.OrdinalIgnoreCase),
            "Evidence must not be written into the user's run directory.");
        var original = File.ReadAllBytes(source);
        var run = JsonSerializer.Deserialize<LivingArcadeRun>(original, LivingArcadeLevelPolicy.JsonOptions)!;
        Assert.IsNotNull(run);
        var raw = run.Levels[run.CurrentLevel];
        var played = LivingArcadePickupSchedule.ForPlay(LivingArcadePacing.ForPlay(raw));
        var referenceFire = LivingArcadeKillability.CreateProfile(run.EntryState, played.Number);
        var difficulty = Math.Max(.8, 1 - Math.Max(0, run.Attempts - 3) * .05);
        var cost = LivingArcadeLevelPolicy.EstimateCost(played);
        Assert.IsTrue(cost.PeakScheduledEnemies <= 80 && cost.EstimatedPeakHostileShots <= 480
            && cost.ExpandedSpawnCount <= 1200);
        var clock = Stopwatch.StartNew();
        var paths = new List<object>();
        var totalActivePeak = 0;
        for (var path = 0; path < 3; path++)
        {
            var simulation = new LivingArcadeSimulation(played, run.EntryState, run.Attempts, run.DeathProgress, run.Penalty);
            var snapshot = simulation.Snapshot;
            var enemyPeak = 0; var activePeak = 0; var shotPeak = 0; var frames = 0;
            var foundValues = new HashSet<int>();
            while (!snapshot.IsComplete && !snapshot.IsDefeated && snapshot.Progress < played.DurationSeconds + 176 && frames++ < 2000)
            {
                var motion = path switch { 0 => 0, 1 => Math.Sin(snapshot.Progress * .4), _ => Math.Sin(snapshot.Progress * 1.1) };
                simulation.Advance(.25, new(Left: motion < 0, Right: motion > 0));
                snapshot = simulation.Snapshot;
                enemyPeak = Math.Max(enemyPeak, snapshot.Enemies.Count);
                activePeak = Math.Max(activePeak, snapshot.Enemies.Count(e => e.ExperienceValue == 5 && e.Y is > 0 and < 1));
                shotPeak = Math.Max(shotPeak, snapshot.Shots.Count(s => s.Hostile));
                foreach (var value in snapshot.Enemies.Select(e => e.ExperienceValue)) foundValues.Add(value);
                Assert.IsTrue(double.IsFinite(snapshot.Health) && double.IsFinite(snapshot.Progress));
            }
            totalActivePeak = Math.Max(totalActivePeak, activePeak);
            Assert.IsTrue(enemyPeak <= 80 && shotPeak <= 480);
            Assert.IsTrue(snapshot.IsComplete || snapshot.IsDefeated, "The finite run must terminate.");
            paths.Add(new { input = path == 0 ? "center" : path == 1 ? "sweep" : "zigzag", frames,
                enemyPeak, activePeak, hostileShotPeak = shotPeak, snapshot.IsComplete, snapshot.IsDefeated,
                snapshot.Progress, remainingHeroHealth = snapshot.Health, snapshot.DamageTaken,
                weightedExperienceEarned = simulation.ResultHero.TotalKills - run.EntryState.TotalKills,
                observedExperienceValues = foundValues.Order().ToArray() });
        }
        Assert.IsTrue(totalActivePeak >= 2, "Several real active attackers must coexist in the played saved level.");
        CollectionAssert.AreEqual(original, File.ReadAllBytes(source), "Read-only play must preserve the user's save bytes.");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "saved-level-combat-evidence.json"), JsonSerializer.Serialize(new
        {
            run.CurrentLevel, readOnlySource = true, sourceSha256 = Convert.ToHexString(SHA256.HashData(original)),
            sourceBytesUnchanged = true, elapsedSeconds = clock.Elapsed.TotalSeconds,
            rawEnemies = raw.Waves.Sum(w => w.Count), playedEnemies = played.Waves.Sum(w => w.Count),
            cost.ExpandedSpawnCount, cost.PeakScheduledEnemies, cost.EstimatedPeakHostileShots,
            referenceFire,
            effectiveEnemyDefinitions = played.EnemyTypes.Select(e => {
                var plan = LivingArcadeKillability.CreateSpawnPlan(e, -.03, referenceFire, difficulty);
                return new { e.Id, originalHealth = e.Health, actualSpawnHealth = plan.Health,
                    plan.Evidence, plan.SpawnY, plan.EntranceHoldSeconds, plan.ForceVulnerable,
                    plan.Adjusted, plan.AdjustmentReason,
                    experienceValue = LivingArcadeCombatRules.ExperienceValue(e),
                    maximumVolleySize = e.States.Max(s => s.ShotInterval > 0 ? s.ShotCount : 0) };
            }), paths
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
