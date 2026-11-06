using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.LivingArcade;

public sealed partial class LivingArcadeGeneration
{
    internal static string ReviewInput(LivingArcadeRun run, LivingArcadeDraft design,
        LivingArcadeLevel candidate, LivingArcadeProbeResult probe, LivingArcadeLevel? evaluatedLevel = null)
    {
        var plan = ReadTypedPlan(design);
        if (plan is not null) ValidatePlanImplementation(candidate, plan);
        var previousEnemies = run.Levels.Values.Where(l => l.Number < candidate.Number).OrderBy(l => l.Number)
            .TakeLast(3).SelectMany(l => l.EnemyTypes).ToArray();
        var previousMotions = previousEnemies.SelectMany(e => e.States).Select(s => s.Motion).ToHashSet(StringComparer.Ordinal);
        var previousCycles = previousEnemies.Select(CycleSignature).ToHashSet(StringComparer.Ordinal);
        return JsonSerializer.Serialize(new {
        task = "Review the CANDIDATE facts, compare them to the intended mechanic and separately labeled PREVIOUS levels. Never attribute previous enemy firing flags to candidate enemies.",
        balanceReference = new { instruction = BalanceInstruction, hero = ModelBaselineHero(run.EntryState) },
        previousLevelsForComparisonOnly = run.Levels.Values.Where(l => l.Number < candidate.Number)
            .OrderBy(l => l.Number).TakeLast(3).Select(l => new {
                scope = "PREVIOUS_LEVEL_NOT_CANDIDATE", levelNumber = l.Number,
                oldBehaviors = l.EnemyTypes.Select(e => new { previousEnemy = $"previous:{l.Number}/{e.Id}",
                    rawHealth = e.Health,
                    killability = HealthEvidence(e, l, run.EntryState),
                    motions = e.States.Select(s => s.Motion).Distinct(), hadShooting = e.States.Any(s => s.ShotInterval > 0),
                    orderedMotionCycle = CycleSignature(e),
                    hadVolley = e.States.Any(s => s.ShotInterval > 0 && s.ShotCount > 1),
                    hadShield = e.States.Any(s => LivingArcadeLevelPolicy.ShieldMode(s)!="off"),
                    hadReturningShots=e.States.Any(s=>s.ShotReturnAfterSeconds>0), e.DeathSpawnId, e.DeathSpawnCount })
            }),
        authoritativeExecutablePlan = plan,
        hostAlreadyVerifiedPlanExactly = plan is not null,
        conceptForFlavorOnly = design.Concept,
        candidateFacts = new {
            scope = "CANDIDATE_TO_REVIEW", candidate.Number, candidate.DurationSeconds,
            enemies = candidate.EnemyTypes.Select(e => new {
                candidateEnemy = $"candidate:{candidate.Number}/{e.Id}", enemyId = e.Id, e.InitialState,
                e.Health, killability = HealthEvidence(e, candidate, run.EntryState),
                e.Speed, reachableCycleStateIds = RepeatingCycle(e),
                executionTimeline = ExecutionTimeline(e),
                states = e.States.Select(s => new { stateId = s.Id, s.Duration, s.NextState, s.Motion,
                    s.ForwardSpeed, s.HorizontalSpeed, firesHostileShots = s.ShotInterval > 0,
                    s.ShotInterval, s.ShotCount, s.ShotSpread, s.ShotSpeed, s.ShieldMode,s.Telegraph,
                    s.WeakSpotAngleDegrees,s.WeakSpotWidthDegrees,s.ShieldRotationDegreesPerSecond,s.ShotReturnAfterSeconds,
                    followsPlayerX = s.Motion == "hunt", transitionTrigger = "timer_only" }),
                splitOnDeath = e.DeathSpawnCount > 0, e.DeathSpawnId, e.DeathSpawnCount
            }),
            waveSchedule = candidate.Waves.Select(w => new { w.At, w.EnemyId, w.Count, w.X, w.Spacing,
                w.SpawnInterval, finalSpawnAt = w.At + (w.Count - 1) * w.SpawnInterval }),
            candidate.MiniEvents
        },
        playableCandidateFacts = evaluatedLevel is null ? null : new {
            scope = "PLAYABLE_COPY_USED_BY_BASELINE_PROBE",
            note = "The host adds support shooters and scheduled pickups after model output. These facts explain the probe; cite mechanic evidence using original candidate enemy/state ids only.",
            evaluatedLevel.DurationSeconds,
            waveCount = evaluatedLevel.Waves.Count,
            scheduledEnemies = evaluatedLevel.Waves.Sum(w => w.Count),
            enemies = evaluatedLevel.EnemyTypes.Select(e => new { e.Id, e.Health,
                killability = HealthEvidence(e, evaluatedLevel, run.EntryState),
                hostAdded = candidate.EnemyTypes.All(original => original.Id != e.Id),
                firesHostileShots = e.States.Any(s => s.ShotInterval > 0),
                hostileShotIntervals = e.States.Where(s => s.ShotInterval > 0).Select(s => s.ShotInterval) }),
            evaluatedLevel.MiniEvents
        },
        machineComparedNewFeatures = new {
            unseenMotions = candidate.EnemyTypes.SelectMany(e => e.States).Select(s => s.Motion)
                .Distinct().Where(m => !previousMotions.Contains(m)),
            introducesDeathSplitting = candidate.EnemyTypes.Any(e => e.DeathSpawnCount > 0)
                && previousEnemies.All(e => e.DeathSpawnCount == 0),
            introducesVolley = candidate.EnemyTypes.Any(e => e.States.Any(s => s.ShotInterval > 0 && s.ShotCount > 1))
                && previousEnemies.All(e => e.States.All(s => s.ShotInterval == 0 || s.ShotCount == 1)),
            unseenOrderedMotionCycles = candidate.EnemyTypes.Select(CycleSignature).Distinct()
                .Where(signature => !previousCycles.Contains(signature))
        },
        measuredCandidateProbe = new {
            evaluationScope = evaluatedLevel is null ? "CALLER_SUPPLIED_PROBE" : "PLAYABLE_COPY_WITHOUT_BOSS_REWARD",
            probe.Accepted, probe.ExpandedSpawnCount, probe.PeakScheduledEnemies,
            probe.EstimatedPeakHostileShots, probe.RuntimeTruncated,
            paths = probe.Trajectories.Select(t => new { t.Name, t.Completed, t.Defeated, t.DamageTaken }) }
        }, LivingArcadeLevelPolicy.JsonOptions);
    }

    private static object HealthEvidence(LivingArcadeEnemyDefinition enemy, LivingArcadeLevel level, LivingArcadeHeroState hero)
    {
        var profile = LivingArcadeKillability.CreateProfile(BaselineHero(hero), level.Number);
        var original = LivingArcadeKillability.Evaluate(enemy, -.03, profile);
        var plan = LivingArcadeKillability.CreateSpawnPlan(enemy, -.03, profile);
        return new
        {
            reference = "Current focused frontal-fire reference at the top entrance, including the slow temporary rocket and time-mode switches; not an assertion about historical player fire or a whole-wave victory guarantee.",
            rawHealth = enemy.Health, referenceFire = profile,
            actualSpawnHealth = plan.Health, spawnBudget = plan.Evidence.Budget,
            guaranteedHits = plan.Evidence.HitCount, firstHitSeconds = plan.Evidence.FirstHitSeconds,
            reachesContact = plan.Evidence.ReachesContact, contactSeconds = plan.Evidence.ContactSeconds,
            forecastDeadlineSeconds = plan.Evidence.DeadlineSeconds,
            originalWindow = original, runtimeGuard = plan.Adjusted, adjustmentReason = plan.AdjustmentReason,
            actualSpawnY = plan.SpawnY, entranceHoldSeconds = plan.EntranceHoldSeconds,
            forceVulnerable = plan.ForceVulnerable
        };
    }

    private static string[] ExecutionTimeline(LivingArcadeEnemyDefinition enemy)
    {
        var rows = new List<string>(); var id = enemy.InitialState; double at = 0;
        for (var i = 0; i < enemy.States.Count + 1; i++)
        {
            var state = enemy.States.Single(s => s.Id == id);
            rows.Add(FormattableString.Invariant($"at age {at:0.##}s: {state.Id} uses {state.Motion}; after {state.Duration:0.##}s automatically enters {state.NextState}"));
            at += state.Duration; id = state.NextState;
        }
        return rows.ToArray();
    }

    private static string CycleSignature(LivingArcadeEnemyDefinition enemy)
    {
        var motions = RepeatingCycle(enemy).Select(id => enemy.States.Single(s => s.Id == id).Motion).ToArray();
        var reduced = motions.Where((motion, i) => i == 0 || motions[i - 1] != motion).ToList();
        if (reduced.Count > 1 && reduced[0] == reduced[^1]) reduced.RemoveAt(reduced.Count - 1);
        return Enumerable.Range(0, reduced.Count).Select(shift => string.Join(">", reduced.Skip(shift).Concat(reduced.Take(shift))))
            .Order(StringComparer.Ordinal).First();
    }

    private static string[] RepeatingCycle(LivingArcadeEnemyDefinition enemy)
    {
        var route = new List<string>(); var current = enemy.InitialState;
        while (!route.Contains(current, StringComparer.Ordinal))
        { route.Add(current); current = enemy.States.Single(s => s.Id == current).NextState; }
        return route.SkipWhile(id => id != current).ToArray();
    }
}
