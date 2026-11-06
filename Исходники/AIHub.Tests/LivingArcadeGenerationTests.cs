using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.LivingArcade;

namespace AIHub.Tests;

[TestClass]
public sealed class LivingArcadeGenerationTests
{
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, LivingArcadeLevelPolicy.JsonOptions);

    [TestMethod]
    public void GeneratedLevelCannotClaimModelAuthorshipOrUserSuppliedProvenance()
    {
        var original = LivingArcadeLevelPolicy.CreateFirst(13) with {
            Number = 2, Authored = true, Producer = "model invented owner", ProducerVersion = "invented-version",
            Credits = [new("invented-role", "invented-model", "invented-revision")] };
        var parsed = LivingArcadeGeneration.ParseLevel(Json(original), 2, 99);
        Assert.IsFalse(parsed.Authored);
        Assert.AreEqual("LOPATA", parsed.Producer);
        Assert.AreEqual(new LivingArcadeLevel().ProducerVersion, parsed.ProducerVersion);
        Assert.AreEqual(0, parsed.Credits.Count);
        Assert.AreEqual(99 + 2 * 7919, parsed.Seed);
    }

    [TestMethod]
    public void WrongLevelNumberAndUnknownExecutablePropertiesAreRejected()
    {
        var first = LivingArcadeLevelPolicy.CreateFirst(9);
        Assert.ThrowsExactly<InvalidDataException>(() => LivingArcadeGeneration.ParseLevel(Json(first), 2, 9));
        var data = Json(first with { Number = 2 });
        data = data.Insert(data.IndexOf('{') + 1, "\"script\":\"execute process\",");
        Assert.ThrowsExactly<JsonException>(() => LivingArcadeGeneration.ParseLevel(data, 2, 9));
    }

    [TestMethod]
    public void ArtistOnlyStylesKnownEnemyIdsAndCannotChangeCombat()
    {
        var level = LivingArcadeLevelPolicy.CreateFirst(3);
        var styles = level.EnemyTypes.Select(e => new LivingArcadeGeneration.ArtStyle(e.Id, "#55AAFF", "star")).ToList();
        var art = LivingArcadeGeneration.ParseArt(Json(new LivingArcadeGeneration.ArtPatch("#111122", styles, [60,64,67,72])), level);
        Assert.AreEqual(level.EnemyTypes.Count, art.EnemyStyles.Count);
        Assert.AreEqual("#101A2C", level.BackgroundColor);
        styles[0] = styles[0] with { Id = "invented_enemy" };
        Assert.ThrowsExactly<InvalidDataException>(() => LivingArcadeGeneration.ParseArt(
            Json(new LivingArcadeGeneration.ArtPatch("#111122", styles, [60])), level));
    }

    [TestMethod]
    public void FirstMiniEventsAreBoundedAndCannotRewriteAuthoredLevel()
    {
        var first = LivingArcadeLevelPolicy.CreateFirst(8);
        var valid = new { events = new[] {
            new LivingArcadeMiniEvent { At = 22, Kind = "bonus", X = .4 },
            new LivingArcadeMiniEvent { At = 48, Kind = "fog", X = .6, Strength = .5 } } };
        Assert.AreEqual(2, LivingArcadeGeneration.ParseFirstEvents(Json(valid), first).Count);
        Assert.AreEqual(4, first.MiniEvents.Count);
        Assert.ThrowsExactly<InvalidDataException>(() => LivingArcadeGeneration.ParseFirstEvents(
            "{\"events\":[],\"waves\":[]}", first));
        Assert.ThrowsExactly<InvalidDataException>(() => LivingArcadeGeneration.ParseFirstEvents(
            "{\"events\":[],\"events\":[]}", first));
    }

    [TestMethod]
    public void CopiedFirstLevelContextProducesSpecificRepairWithoutLosingLocalizedErrorKey()
    {
        var first = LivingArcadeLevelPolicy.CreateFirst(8);
        var copied = new { durationSeconds = 120, seed = 8, enemyIds = new[] { "scout" },
            existingEvents = first.MiniEvents, events = new[] {
                new LivingArcadeMiniEvent { At = 22, Kind = "bonus", X = .4 },
                new LivingArcadeMiniEvent { At = 48, Kind = "fog", X = .6, Strength = .5 } } };
        var error = Assert.ThrowsExactly<InvalidDataException>(() => LivingArcadeGeneration.ParseFirstEvents(Json(copied), first));
        Assert.AreEqual("Arcade.Ai.InvalidOutput", error.Message);
        StringAssert.Contains(error.Data["ValidationDetail"] as string, "durationSeconds, seed, enemyIds, existingEvents");
        StringAssert.Contains(error.Data["ValidationDetail"] as string, "without copying the input");
    }

    [TestMethod]
    public void CriticCannotApproveATemplateOrAnUnimplementedMechanicOrInventEvidence()
    {
        var level = LivingArcadeLevelPolicy.CreateFirst(8);
        var enemy = level.EnemyTypes[0];
        var review = new LivingArcadeGeneration.Verdict(false,
            "The scout only advances; the proposed movement reaction is not implemented.", false, false,
            [new(enemy.Id, enemy.States[0].Id, "The state moves straight and cannot react to player movement.")]);
        Assert.IsFalse(LivingArcadeGeneration.ParseVerdict(Json(review), level).Approved);
        Assert.ThrowsExactly<InvalidDataException>(() => LivingArcadeGeneration.ParseVerdict(Json(review with { Approved = true }), level));
        Assert.ThrowsExactly<InvalidDataException>(() => LivingArcadeGeneration.ParseVerdict(Json(review with {
            Reason = "brief concrete assessment", MechanicImplemented = true, MeaningfullyNew = true, Approved = true }), level));
        Assert.ThrowsExactly<InvalidDataException>(() => LivingArcadeGeneration.ParseVerdict(Json(review with {
            Evidence = [new("invented", "fake", "This alleged enemy is not present in the actual level.")] }), level));
        Assert.ThrowsExactly<InvalidDataException>(() => LivingArcadeGeneration.ParseVerdict(Json(review), level, exactPlanVerified: true));
        var validRejection = review with { MechanicImplemented = true,
            Reason = "The plan is implemented but the introductions are too dense for this player." };
        Assert.IsFalse(LivingArcadeGeneration.ParseVerdict(Json(validRejection), level, exactPlanVerified: true).Approved);
    }

    [TestMethod]
    public void CriticReceivesCandidateShootingFactsSeparatelyFromPreviousEnemyNames()
    {
        var previous = LivingArcadeLevelPolicy.CreateFirst(8);
        var candidate = previous with { Number = 2,
            EnemyTypes = previous.EnemyTypes.Select(e => e with {
                States = e.States.Select(s => s with { ShotInterval = 2, Shield = false }).ToList() }).ToList() };
        var run = new LivingArcadeRun { Levels = new() { [1] = previous } };
        var probe = LivingArcadeLevelPolicy.Probe(candidate);
        using var card = JsonDocument.Parse(LivingArcadeGeneration.ReviewInput(run,
            new() { Number = 2, Concept = "A timer challenge", NewMechanic = "Timed volleys" }, candidate, probe));
        var oldScout = card.RootElement.GetProperty("previousLevelsForComparisonOnly")[0].GetProperty("oldBehaviors")[0];
        var newScout = card.RootElement.GetProperty("candidateFacts").GetProperty("enemies")[0];
        Assert.IsFalse(oldScout.GetProperty("hadShooting").GetBoolean());
        Assert.IsTrue(newScout.GetProperty("states")[0].GetProperty("firesHostileShots").GetBoolean());
        Assert.AreEqual("candidate:2/scout", newScout.GetProperty("candidateEnemy").GetString());
        Assert.AreEqual("timer_only", newScout.GetProperty("states")[0].GetProperty("transitionTrigger").GetString());
        Assert.IsFalse(card.RootElement.GetProperty("hostAlreadyVerifiedPlanExactly").GetBoolean());
    }

    [TestMethod]
    public void TypedDesignerCannotAddTriggersAndProgrammerMustMatchTheActualRepeatingCycle()
    {
        var first = LivingArcadeLevelPolicy.CreateFirst(8);
        var plan = new LivingArcadeGeneration.MechanicPlan(["zigzag", "orbit"], 5, 3, 2, 30, 0);
        var draft = LivingArcadeGeneration.ParseTypedDraft(Json(new { number = 2, concept = "Two moving rhythms", plan }), 2);
        Assert.IsNotNull(LivingArcadeGeneration.ReadTypedPlan(draft));
        Assert.IsTrue(draft.NewMechanic.Length <= 250);
        Assert.IsNull(LivingArcadeGeneration.ReadTypedPlan(new() { Number = 2, NewMechanic = "Legacy prose only" }));
        Assert.ThrowsExactly<JsonException>(() => LivingArcadeGeneration.ParseTypedDraft(
            Json(new { number = 2, concept = "Unsupported", plan, onPlayerHit = "increase speed" }), 2));
        var enemies = first.EnemyTypes.ToList();
        enemies[0] = enemies[0] with { InitialState = "turn", States = [
            new() { Id = "turn", NextState = "circle", Duration = 5, Motion = "zigzag", HorizontalSpeed = .15, ShotInterval = 3, ShotCount = 2, ShotSpread = 30 },
            new() { Id = "circle", NextState = "turn", Duration = 5, Motion = "orbit", ShotInterval = 3, ShotCount = 2, ShotSpread = 30 } ] };
        var candidate = first with { Number = 2, EnemyTypes = enemies };
        LivingArcadeGeneration.ValidatePlanImplementation(candidate, plan);
        candidate.EnemyTypes[0].States[0].ShotInterval = 0;
        Assert.ThrowsExactly<InvalidDataException>(() => LivingArcadeGeneration.ValidatePlanImplementation(candidate, plan));
        Assert.ThrowsExactly<InvalidDataException>(() => LivingArcadeGeneration.ReviewInput(new() { Levels = new() { [1] = first } },
            draft, candidate, LivingArcadeLevelPolicy.Probe(candidate)));
    }

    [TestMethod]
    public void TypedPlanMustMoveSpawnAndFinishItsCycleBeforeLeavingTheScreen()
    {
        var first = LivingArcadeLevelPolicy.CreateFirst(8);
        var plan = new LivingArcadeGeneration.MechanicPlan(["zigzag"], 3, 5, 1, 0, 0);
        var lead = first.EnemyTypes[0] with { Speed = .05, InitialState = "p0", States = [
            new() { Id = "p0", NextState = "p0", Motion = "zigzag", Duration = 3,
                HorizontalSpeed = .15, ForwardSpeed = 1, ShotInterval = 5 } ] };
        var candidate = first with { Number = 2, EnemyTypes = [lead] };
        candidate.Waves = [new() { At = 10, EnemyId = lead.Id, Count = 1 }]; candidate.MiniEvents = [];
        LivingArcadeGeneration.ValidatePlanImplementation(candidate, plan);
        lead.States[0].HorizontalSpeed = 0;
        Assert.ThrowsExactly<InvalidDataException>(() => LivingArcadeGeneration.ValidatePlanImplementation(candidate, plan));
        lead.States[0].HorizontalSpeed = .15; candidate.Waves = [];
        Assert.ThrowsExactly<InvalidDataException>(() => LivingArcadeGeneration.ValidatePlanImplementation(candidate, plan));
        candidate.Waves = [new() { At = 10, EnemyId = lead.Id, Count = 1 }];
        lead.Speed = .2; lead.States[0].ForwardSpeed = 3;
        Assert.ThrowsExactly<InvalidDataException>(() => LivingArcadeGeneration.ValidatePlanImplementation(candidate, plan));
    }

    [TestMethod]
    public void TruncatedModelOutputIsNotAcceptedAsACompleteLevel()
    {
        var raw = JsonSerializer.Serialize(new { choices = new[] { new {
            finish_reason = "length", message = new { content = "{}" } } } });
        Assert.ThrowsExactly<InvalidDataException>(() => LivingArcadeGeneration.ReadCompletion(raw));
        var complete = JsonSerializer.Serialize(new { choices = new[] { new {
            finish_reason = "stop", message = new { content = "{\"number\":2}" } } } });
        Assert.AreEqual("{\"number\":2}", LivingArcadeGeneration.ReadCompletion(complete));
    }

    [TestMethod]
    public void BothPinnedModelsHaveExactMonolithicFileHashesAndDistinctLicenses()
    {
        var cards = LivingArcadeModels.CreateCards(Path.GetTempPath());
        Assert.AreEqual(2, cards.Count);
        Assert.AreEqual("Apache-2.0", cards[0].License);
        Assert.AreEqual("Qwen Research", cards[1].License);
        Assert.AreEqual(LivingArcadeModels.MainBytes + LivingArcadeModels.ArtistBytes, cards.Sum(c => c.TotalBytes));
        Assert.IsTrue(cards.All(c => c.Files.Count == 1 && c.Files[0].Sha256?.Length == 64));
        Assert.IsTrue(cards.All(c => c.RepositoryId.StartsWith("Qwen/", StringComparison.Ordinal)));
    }
}
