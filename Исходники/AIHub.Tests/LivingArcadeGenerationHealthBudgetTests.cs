using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services.LivingArcade;

namespace AIHub.Tests;

[TestClass]
public sealed class LivingArcadeGenerationHealthBudgetTests
{
    [TestMethod]
    [DataRow(0d, false)]
    [DataRow(3d, false)]
    [DataRow(0d, true)]
    [DataRow(3d, true)]
    public void BothModelRolesRetainTheBroadHealthChoicesRegardlessOfShootingOrSize(double shotInterval, bool rareLarge)
    {
        var plan = new LivingArcadeGeneration.MechanicPlan(["line"], 5, shotInterval, 1, 0, 0)
        { RareLarge = rareLarge };
        var format = JsonSerializer.SerializeToElement(LivingArcadeGeneration.LevelResponseFormat(7, plan));
        var enemySchemas = format.GetProperty("json_schema").GetProperty("schema").GetProperty("properties")
            .GetProperty("enemyTypes").GetProperty("items");
        var leadHealth = enemySchemas[0].GetProperty("properties").GetProperty("health").GetProperty("enum")
            .EnumerateArray().Select(value => value.GetDouble()).ToArray();
        var supportHealth = enemySchemas[1].GetProperty("properties").GetProperty("health").GetProperty("enum")
            .EnumerateArray().Select(value => value.GetDouble()).ToArray();

        CollectionAssert.AreEqual(leadHealth, supportHealth);
        Assert.AreEqual(.25, leadHealth.Min()); Assert.AreEqual(200d, leadHealth.Max());
        foreach (var health in new[] { .25, 1, 2, 3, 4, 5, 6, 20, 50, 100, 150, 200 })
            Assert.IsTrue(leadHealth.Contains(health), "Raw model HP must remain a broad choice, not a role-specific combat ceiling.");
    }

    [TestMethod]
    public void GenerationContractExpressesTheSingleContactWindowRuleWithoutRoleHealthBands()
    {
        var profile = JsonSerializer.SerializeToElement(LivingArcadeGeneration.GenerationCapabilities);
        Assert.IsFalse(profile.TryGetProperty("generatedEnemyHealthCaps", out _));
        var rule = profile.GetProperty("generatedEnemyHealthRule");
        StringAssert.Contains(rule.GetProperty("constraint").GetString()!, "continuous connected fire");
        StringAssert.Contains(rule.GetProperty("constraint").GetString()!, "visible appearance before player contact");
        StringAssert.Contains(rule.GetProperty("referenceFire").GetString()!, "weak/jam");
        StringAssert.Contains(rule.GetProperty("runtimeApplication").GetString()!, "keeps that original data");
        StringAssert.Contains(rule.GetProperty("shields").GetString()!, "before contact");

        var prompt = (string)typeof(LivingArcadeGeneration).GetField("ProgrammerPrompt",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
        StringAssert.Contains(prompt, "ONLY combat-HP restriction");
        StringAssert.Contains(prompt, "lowering HP alone cannot fix an invulnerable approach");
        Assert.IsFalse(prompt.Contains("ordinary cap", StringComparison.Ordinal));
        Assert.IsFalse(prompt.Contains("health1..6", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CriticSeesTheActualContactBudgetWhileLegacyRawHealthRemainsUnchanged()
    {
        var candidate = new LivingArcadeLevel { Number = 7, EnemyTypes = [
            Enemy("ordinary", false), Enemy("active", true), Enemy("large", true, "rare-large"),
            Enemy("stationary", false) with { States = [new() { Motion = "hold", ForwardSpeed = 0 }] } ] };
        var previous = candidate with { Number = 6 };
        var run = new LivingArcadeRun { Levels = new() { [6] = previous } };
        var original = JsonSerializer.Serialize(candidate, LivingArcadeLevelPolicy.JsonOptions);
        var probe = new LivingArcadeProbeResult(true, null, 3, 3, 1, false, 0, 0, []);
        using var review = JsonDocument.Parse(LivingArcadeGeneration.ReviewInput(run,
            new() { Number = 7, Concept = "Health budget evidence", NewMechanic = "Timed volleys" }, candidate, probe));
        var candidateRows = review.RootElement.GetProperty("candidateFacts").GetProperty("enemies");
        var previousRows = review.RootElement.GetProperty("previousLevelsForComparisonOnly")[0].GetProperty("oldBehaviors");
        for (var index = 0; index < candidate.EnemyTypes.Count; index++)
        {
            var enemy = candidate.EnemyTypes[index];
            var currentPlan = LivingArcadeKillability.CreateSpawnPlan(enemy, -.03,
                LivingArcadeKillability.CreateProfile(run.EntryState, candidate.Number));
            var previousPlan = LivingArcadeKillability.CreateSpawnPlan(enemy, -.03,
                LivingArcadeKillability.CreateProfile(run.EntryState, previous.Number));
            var currentEvidence = candidateRows[index].GetProperty("killability");
            var previousEvidence = previousRows[index].GetProperty("killability");
            Assert.AreEqual(200d, candidateRows[index].GetProperty("health").GetDouble());
            Assert.AreEqual(currentPlan.Health, currentEvidence.GetProperty("actualSpawnHealth").GetDouble(), 1e-9);
            Assert.AreEqual(currentPlan.Evidence.Budget, currentEvidence.GetProperty("spawnBudget").GetDouble(), 1e-9);
            Assert.AreEqual(currentPlan.Evidence.HitCount, currentEvidence.GetProperty("guaranteedHits").GetInt32());
            Assert.AreEqual(currentPlan.Evidence.DeadlineSeconds, currentEvidence.GetProperty("forecastDeadlineSeconds").GetDouble(), 1e-9);
            Assert.AreEqual(currentPlan.Evidence.ReachesContact, currentEvidence.GetProperty("reachesContact").GetBoolean());
            if (currentPlan.Evidence.ReachesContact)
            {
                Assert.IsTrue(currentPlan.Health > 0 && currentPlan.Health < enemy.Health);
                Assert.AreEqual(currentPlan.Evidence.ContactSeconds!.Value,
                    currentEvidence.GetProperty("contactSeconds").GetDouble(), 1e-9);
            }
            else
            {
                Assert.AreEqual(200d, currentPlan.Health, "A non-approaching target has no contact HP ceiling.");
                Assert.AreEqual(JsonValueKind.Null, currentEvidence.GetProperty("contactSeconds").ValueKind);
            }
            Assert.AreEqual(200d, previousRows[index].GetProperty("rawHealth").GetDouble());
            Assert.AreEqual(previousPlan.Health, previousEvidence.GetProperty("actualSpawnHealth").GetDouble(), 1e-9);
        }
        Assert.AreEqual(original, JsonSerializer.Serialize(candidate, LivingArcadeLevelPolicy.JsonOptions),
            "Constructing the critic request must not rewrite model output or an old saved definition.");
    }

    [TestMethod]
    public void ANewApproachWithNoShieldOpeningIsRejectedWhileNonContactHealthIsNotArbitrarilyLimited()
    {
        var enemy = Enemy("blind", false) with
        {
            Speed = .2, States = [new()
            {
                ForwardSpeed = 3, Duration = 2, ShieldMode = "rotating", WeakSpotAngleDegrees = 0,
                WeakSpotWidthDegrees = 20, ShieldRotationDegreesPerSecond = 0
            }]
        };
        var level = new LivingArcadeLevel
        {
            Number = 7, DurationSeconds = 120, EnemyTypes = [enemy], Mechanics = ["Shielded approach"],
            Waves = Enumerable.Range(0, 38).Select(index => new LivingArcadeWave
                { At = 2 + index * 3, Count = 2, EnemyId = "blind" }).ToList()
        };
        var error = Assert.ThrowsExactly<InvalidDataException>(() => LivingArcadeGeneration.ParseProgrammedLevel(
            JsonSerializer.Serialize(level, LivingArcadeLevelPolicy.JsonOptions), 7, 12));
        StringAssert.Contains((string)error.Data["ValidationDetail"]!, "before player contact");

        level.EnemyTypes[0] = enemy with { Health = 200, States = [new() { Motion = "hold", ForwardSpeed = 0 }] };
        var parsed = LivingArcadeGeneration.ParseProgrammedLevel(
            JsonSerializer.Serialize(level, LivingArcadeLevelPolicy.JsonOptions), 7, 12);
        Assert.AreEqual(200d, parsed.EnemyTypes.Single().Health);
    }

    private static LivingArcadeEnemyDefinition Enemy(string id, bool shooting, string encounterClass = "normal") => new()
    {
        Id = id, Health = 200, EncounterClass = encounterClass, InitialState = "advance",
        States = [new() { Id = "advance", NextState = "advance", ShotInterval = shooting ? 3 : 0 }]
    };
}
