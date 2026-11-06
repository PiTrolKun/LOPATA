using System.Reflection;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services.LivingArcade;

namespace AIHub.Tests;

[TestClass]
public sealed class LivingArcadeGenerationBaselineTests
{
    [TestMethod]
    public void DesignerAndProgrammerContextDoesNotDependOnRandomBossRewards()
    {
        var plain = Run();
        var rewarded = WithReward(plain);
        var saved = JsonSerializer.Serialize(rewarded);
        var context = Context(plain, 4);
        Assert.AreEqual(context, Context(rewarded, 4));
        using var json = JsonDocument.Parse(context);
        var hero = json.RootElement.GetProperty("hero");
        Assert.IsFalse(hero.TryGetProperty("activeBossReward", out _));
        Assert.IsFalse(json.RootElement.TryGetProperty("bossRewardForNextStages", out _));
        StringAssert.Contains(json.RootElement.GetProperty("balanceProfile").GetString()!, "WITHOUT any random boss reward");
        Assert.AreEqual(plain.EntryState.TotalKills, hero.GetProperty("totalKills").GetInt64());
        Assert.AreEqual(saved, JsonSerializer.Serialize(rewarded), "Building model inputs must preserve the real reward and saved progression.");
    }

    [TestMethod]
    public void CriticReceivesBaselineEvidenceAndTheAdaptedPlayableCandidate()
    {
        var run = Run(); var rewarded = WithReward(run);
        var candidate = Candidate();
        var original = JsonSerializer.Serialize(candidate);
        var playable = LivingArcadeGeneration.PlayableCandidate(candidate);
        var design = new LivingArcadeDraft { Number = 2, Concept = "Changing paths", NewMechanic = "Timed movement" };
        var probe = new LivingArcadeProbeResult(true, null, 7, 4, 12, false, 0, 0, []);
        var plain = LivingArcadeGeneration.ReviewInput(run, design, candidate, probe, playable);
        Assert.AreEqual(plain, LivingArcadeGeneration.ReviewInput(rewarded, design, candidate, probe, playable));
        using var json = JsonDocument.Parse(plain);
        Assert.AreEqual("PLAYABLE_COPY_WITHOUT_BOSS_REWARD", json.RootElement.GetProperty("measuredCandidateProbe")
            .GetProperty("evaluationScope").GetString());
        var played = json.RootElement.GetProperty("playableCandidateFacts");
        Assert.AreEqual(playable.Waves.Count, played.GetProperty("waveCount").GetInt32());
        Assert.AreEqual(playable.MiniEvents.Count, played.GetProperty("miniEvents").GetArrayLength());
        Assert.IsTrue(played.GetProperty("enemies").EnumerateArray().Any(e => e.GetProperty("hostAdded").GetBoolean()),
            "The critic must see the host-added shooters that contributed to the probe.");
        Assert.IsFalse(json.RootElement.GetProperty("balanceReference").GetProperty("hero").TryGetProperty("activeBossReward", out _));
        Assert.AreEqual(original, JsonSerializer.Serialize(candidate));
        Assert.IsNotNull(rewarded.EntryState.ActiveBossReward);
    }

    [TestMethod]
    public void AutomaticProbeUsesPlayableCopyWithoutTheLiveReward()
    {
        var candidate = Candidate();
        var hero = WithReward(Run()).EntryState;
        var saved = JsonSerializer.Serialize(hero);
        var playable = LivingArcadeGeneration.PlayableCandidate(candidate);
        var expected = LivingArcadeLevelPolicy.Probe(playable, LivingArcadeGeneration.BaselineHero(hero));
        var actual = LivingArcadeGeneration.ProbeBaselineCandidate(candidate, hero);
        var cost = LivingArcadeLevelPolicy.EstimateCost(playable);
        Assert.AreEqual(expected.Accepted, actual.Accepted);
        Assert.AreEqual(expected.RejectionKey, actual.RejectionKey);
        Assert.AreEqual(cost.ExpandedSpawnCount, actual.ExpandedSpawnCount);
        Assert.AreEqual(cost.PeakScheduledEnemies, actual.PeakScheduledEnemies);
        Assert.AreEqual(cost.EstimatedPeakHostileShots, actual.EstimatedPeakHostileShots);
        Assert.IsFalse(expected.RuntimeTruncated); Assert.IsFalse(actual.RuntimeTruncated);
        Assert.AreEqual(JsonSerializer.Serialize(expected.Trajectories), JsonSerializer.Serialize(actual.Trajectories));
        Assert.AreEqual(saved, JsonSerializer.Serialize(hero));
    }

    [TestMethod]
    public void LegacyUnfinishedCheckpointDropsRewardBiasedWorkButPreservesTheConfirmedReward()
    {
        var checkpoint = LegacyCheckpoint();
        var saved = checkpoint.GetRawText();
        var result = Restore(checkpoint);
        Assert.AreEqual(LivingArcadeGeneration.BalanceProfileVersion, result.GetProperty("BalanceProfile").GetInt32());
        foreach (var name in new[] { "InputContext", "DraftContext", "Design", "Candidate", "Probe", "ReviewReason",
            "Rejection", "Level", "Art", "Draft" })
            Assert.AreEqual(JsonValueKind.Null, result.GetProperty(name).ValueKind, name + " must be rebuilt using the baseline.");
        Assert.AreEqual(0, result.GetProperty("ProgramAttempt").GetInt32());
        Assert.AreEqual(0, result.GetProperty("Attempts").GetArrayLength());
        Assert.AreEqual("Confirmed reward request", result.GetProperty("RewardContext").GetString());
        Assert.AreEqual(LivingArcadeBossRewardTests.Reward().TitleEn,
            result.GetProperty("BossReward").GetProperty("TitleEn").GetString());
        Assert.AreEqual(saved, checkpoint.GetRawText());
    }

    [TestMethod]
    public void BaselineCheckpointRetainsAlreadyConfirmedOrdinaryStages()
    {
        var checkpoint = LegacyCheckpoint(LivingArcadeGeneration.BalanceProfileVersion);
        var result = Restore(checkpoint);
        Assert.AreEqual("Legacy model context", result.GetProperty("InputContext").GetString());
        Assert.AreEqual(4, result.GetProperty("Level").GetProperty("Number").GetInt32());
        Assert.AreEqual(1, result.GetProperty("ProgramAttempt").GetInt32());
    }

    [TestMethod]
    public void AllBalanceRolesExplicitlyEvaluateBaselineAndTreatBotDefeatAsEvidence()
    {
        foreach (var name in new[] { "DesignerPrompt", "ProgrammerPrompt", "CriticPrompt" })
        {
            var prompt = (string)typeof(LivingArcadeGeneration).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            StringAssert.Contains(prompt, LivingArcadeGeneration.BalanceInstruction);
            StringAssert.Contains(prompt, "never a reason to raise enemy health");
            StringAssert.Contains(prompt, "not an automatic rejection");
        }
    }

    private static LivingArcadeRun Run() => new()
    {
        Id = "baseline-fixture", Seed = 12, CurrentLevel = 3,
        EntryState = new() { PowerupBalanceVersion = LivingArcadeHeroBalance.Version,
            TotalKills = 174, MaxHealth = 114, WeaponKind = "shotgun", WeaponPower = 1.1, FireRate = 5.2 }
    };

    private static LivingArcadeRun WithReward(LivingArcadeRun run)
    {
        var reward = LivingArcadeBossRewardTests.Reward();
        return run with { EntryState = run.EntryState with { ActiveBossReward = new()
            { Reward = reward, StartLevel = 4, RemainingLevels = 2 } }, BossRewards = new() { [3] = reward } };
    }

    private static LivingArcadeLevel Candidate() => new()
    {
        Number = 2, DurationSeconds = 30, Seed = 12, Mechanics = ["Timed movement"],
        EnemyTypes = [new() { Id = "lead", Health = 2, Speed = .2,
            States = [new() { ShotInterval = 5 }] }, new() { Id = "support", Health = 1, Speed = .2 }],
        Waves = [new() { At = 2, EnemyId = "lead", Count = 1 },
            new() { At = 5, EnemyId = "support", Count = 2 }, new() { At = 13, EnemyId = "support", Count = 2 },
            new() { At = 21, EnemyId = "support", Count = 2 }]
    };

    private static string Context(LivingArcadeRun run, int number) => (string)typeof(LivingArcadeGeneration)
        .GetMethod("Context", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [run, number])!;

    private static JsonElement LegacyCheckpoint(int balanceProfile = 0)
    {
        var level = LivingArcadeLevelPolicy.CreateFirst(12) with { Number = 4, Authored = false };
        var probe = new LivingArcadeProbeResult(true, null, 3, 3, 0, false, 0, 0, []);
        return JsonSerializer.SerializeToElement(new {
            RunId = "baseline-fixture", CurrentLevel = 3, BalanceProfile = balanceProfile,
            InputContext = "Legacy model context", DraftContext = "Legacy draft context",
            Design = new LivingArcadeDraft { Number = 4, Concept = "Old idea", NewMechanic = "Old plan" },
            Candidate = level, Probe = probe, ReviewReason = "Approved with the old reward", ProgramAttempt = 1,
            Rejection = "Old rejected attempt", Attempts = new[] { new { Candidate = level, Probe = probe, Approved = true, Reason = "Old review" } },
            Level = level, Art = new LivingArcadeGeneration.ArtPatch("#111122",
                level.EnemyTypes.Select(e => new LivingArcadeGeneration.ArtStyle(e.Id, e.Color, e.Shape)).ToList(), [60, 64, 67]),
            Draft = new LivingArcadeDraft { Number = 5, Concept = "Old hint", NewMechanic = "Old hint mechanic" },
            BossReward = LivingArcadeBossRewardTests.Reward(), RewardContext = "Confirmed reward request"
        });
    }

    private static JsonElement Restore(JsonElement checkpoint)
    {
        var request = new LivingArcadeGenerationRequest("baseline-fixture", 3, new("main.gguf", "artist.gguf"), "ru");
        var state = new BackgroundOperationState { Kind = LivingArcadeGeneration.Kind, Title = "Preparing", Checkpoint = checkpoint };
        var result = typeof(LivingArcadeGeneration).GetMethod("ReadCheckpoint", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [state, request])!;
        return JsonSerializer.SerializeToElement(result, result.GetType());
    }
}
