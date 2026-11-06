using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services.LivingArcade;

namespace AIHub.Tests;

[TestClass]
public sealed class LivingArcadeCombatPersistenceTests
{
    private string _root = null!;

    [TestInitialize]
    public void Setup() => _root = Path.Combine(Path.GetTempPath(), "lopata-arcade-combat-" + Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        var path = Path.GetFullPath(_root);
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(path).StartsWith("lopata-arcade-combat-", StringComparison.Ordinal))
            throw new InvalidOperationException();
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }

    private LivingArcadeRun Prepare(LivingArcadeRunStore store) => store.Update(store.Create("Combat", 104).Id, run =>
        run.EntryState = new()
        {
            PowerupBalanceVersion = LivingArcadeHeroBalance.Version,
            WeaponPower = 1.1, FireRate = 5.2, PermanentPower = 1.05, MaxHealth = 104,
            Companions = [new() { Id = "original", Kind = "drone", Health = 24, MaxHealth = 24 }]
        });

    [TestMethod]
    public void WeaponAndCompanionDamagePersistWithoutAnyNewKillAndWithoutSharingTheSourceList()
    {
        var store = new LivingArcadeRunStore(_root);
        var run = Prepare(store);
        var live = LivingArcadeHeroBalance.Copy(run.EntryState);
        live.WeaponKind = "shotgun";
        live.Companions[0].Health = 12;
        store.RecordCombatProgress(run.Id, live);

        var saved = new LivingArcadeRunStore(_root).Load(run.Id)!;
        Assert.AreEqual(0L, saved.EntryState.TotalKills);
        Assert.AreEqual("shotgun", saved.EntryState.WeaponKind);
        Assert.AreEqual(12d, saved.EntryState.Companions.Single().Health);
        Assert.AreEqual(24d, run.EntryState.Companions.Single().Health);
        live.Companions[0].Health = 1;
        live.Companions.Clear();
        Assert.AreEqual(12d, store.Load(run.Id)!.EntryState.Companions.Single().Health);

        var resumed = new LivingArcadeSimulation(saved.Levels[1], saved.EntryState).Snapshot;
        Assert.AreEqual("shotgun", resumed.WeaponKind);
        Assert.AreEqual(.5, resumed.Companions.Single().HealthFraction, .0001);
        Assert.AreEqual(12d, resumed.Companions.Single().Health);
        Assert.AreEqual(24d, resumed.Companions.Single().MaxHealth);
    }

    [TestMethod]
    public void ACompanionKilledWithoutNewExperienceIsNotResurrectedByDefeatOrRetry()
    {
        var store = new LivingArcadeRunStore(_root);
        var run = Prepare(store);
        var live = LivingArcadeHeroBalance.Copy(run.EntryState);
        live.Companions.Clear();
        store.RecordCombatProgress(run.Id, live);
        var afterDeath = store.Load(run.Id)!;
        Assert.AreEqual(0, afterDeath.EntryState.Companions.Count);
        Assert.AreEqual(0L, afterDeath.EntryState.TotalKills);

        store.RecordDefeat(run.Id, 30, live);
        var retry = new LivingArcadeRunStore(_root).Load(run.Id)!;
        Assert.AreEqual(1, retry.Attempts);
        Assert.AreEqual(0, retry.EntryState.Companions.Count);
        Assert.AreEqual(0, new LivingArcadeSimulation(retry.Levels[1], retry.EntryState,
            attempts: retry.Attempts, deathProgress: retry.DeathProgress, penalty: retry.Penalty).Snapshot.Companions.Count);
    }

    [TestMethod]
    public void CombatCheckpointKeepsTheCommittedBaseAndActiveRewardRatherThanLivePickupBuffs()
    {
        var store = new LivingArcadeRunStore(_root);
        var run = Prepare(store);
        var reward = LivingArcadeGeneration.ParseBossReward("""
            {"titleRu":"Волна","titleEn":"Wave","descriptionRu":"Волнистые снаряды и больше урона.",
            "descriptionEn":"Waving projectiles and more damage.","durationLevels":2,"damageMultiplier":1.4,
            "fireRateMultiplier":1,"shotPattern":"wave","pierce":0,"colorHex":"#F8D16D"}
            """);
        run = store.Update(run.Id, value => value.EntryState.ActiveBossReward = new()
            { Reward = reward, RemainingLevels = 2, StartLevel = 2 });
        var rewardBefore = JsonSerializer.Serialize(run.EntryState.ActiveBossReward);
        var live = LivingArcadeHeroBalance.Copy(run.EntryState);
        live.TotalKills = 7;
        live.WeaponKind = "smg";
        live.WeaponPower = LivingArcadeHeroBalance.MaximumWeaponPower;
        live.FireRate = LivingArcadeHeroBalance.MaximumFireRate;
        live.PermanentPower = LivingArcadeHeroBalance.MaximumPermanentPower;
        live.MaxHealth = LivingArcadeHeroBalance.MaximumHealth;
        live.SpreadShots = 3;
        live.ActiveBossReward = null;
        store.RecordCombatProgress(run.Id, live);

        var saved = store.Load(run.Id)!.EntryState;
        Assert.AreEqual(7L, saved.TotalKills);
        Assert.AreEqual("smg", saved.WeaponKind);
        Assert.AreEqual(run.EntryState.WeaponPower, saved.WeaponPower);
        Assert.AreEqual(run.EntryState.FireRate, saved.FireRate);
        Assert.AreEqual(run.EntryState.PermanentPower, saved.PermanentPower);
        Assert.AreEqual(run.EntryState.MaxHealth, saved.MaxHealth);
        Assert.AreEqual(1, saved.SpreadShots);
        Assert.AreEqual(rewardBefore, JsonSerializer.Serialize(saved.ActiveBossReward));
        Assert.AreEqual(3, live.SpreadShots, "Checkpointing must not rewrite the live simulation's input object.");
        Assert.IsNull(live.ActiveBossReward);
    }

    [TestMethod]
    public void DefeatCommitsCombatFieldsButDoesNotCommitUnfinishedBasePickups()
    {
        var store = new LivingArcadeRunStore(_root);
        var run = Prepare(store);
        var live = LivingArcadeHeroBalance.Copy(run.EntryState);
        live.TotalKills = 15;
        live.WeaponKind = "smg";
        live.Companions[0].Health = 6;
        live.WeaponPower = LivingArcadeHeroBalance.MaximumWeaponPower;
        var defeated = store.RecordDefeat(run.Id, 35, live);
        Assert.AreEqual(15L, defeated.EntryState.TotalKills);
        Assert.AreEqual("smg", defeated.EntryState.WeaponKind);
        Assert.AreEqual(6d, defeated.EntryState.Companions.Single().Health);
        Assert.AreEqual(run.EntryState.WeaponPower, defeated.EntryState.WeaponPower);
        Assert.AreEqual(24d, run.EntryState.Companions.Single().Health);
    }

    [TestMethod]
    public void AnOlderExperienceCheckpointCannotRestoreAnOldWeaponOrDeadHelper()
    {
        var store = new LivingArcadeRunStore(_root);
        var run = Prepare(store);
        var latest = LivingArcadeHeroBalance.Copy(run.EntryState);
        latest.TotalKills = 30;
        latest.WeaponKind = "shotgun";
        latest.Companions.Clear();
        store.RecordCombatProgress(run.Id, latest);
        var committed = store.Load(run.Id)!;

        store.RecordCombatProgress(run.Id, run.EntryState);
        var saved = store.Load(run.Id)!;
        Assert.AreEqual(committed.Revision, saved.Revision);
        Assert.AreEqual(30L, saved.EntryState.TotalKills);
        Assert.AreEqual("shotgun", saved.EntryState.WeaponKind);
        Assert.AreEqual(0, saved.EntryState.Companions.Count);
    }

    [TestMethod]
    public void RepeatingTheSameCheckpointDoesNotRewriteTheManifest()
    {
        var store = new LivingArcadeRunStore(_root);
        var run = Prepare(store);
        var changed = LivingArcadeHeroBalance.Copy(run.EntryState);
        changed.TotalKills = 5;
        changed.Companions[0].Kind = "gunner";
        changed.Companions[0].MaxHealth = 30;
        store.RecordCombatProgress(run.Id, changed);
        var first = store.Load(run.Id)!;
        var manifest = File.ReadAllBytes(Path.Combine(_root, run.Id, "run.json"));
        store.RecordCombatProgress(run.Id, changed);
        var repeated = store.Load(run.Id)!;
        Assert.AreEqual(first.Revision, repeated.Revision);
        Assert.AreEqual(first.UpdatedUtc, repeated.UpdatedUtc);
        CollectionAssert.AreEqual(manifest, File.ReadAllBytes(Path.Combine(_root, run.Id, "run.json")));
        Assert.AreEqual("gunner", repeated.EntryState.Companions.Single().Kind);
        Assert.AreEqual(30d, repeated.EntryState.Companions.Single().MaxHealth);
    }

    [TestMethod]
    public void CompletedAndDeletedRunsIgnoreLateCombatCheckpoints()
    {
        var store = new LivingArcadeRunStore(_root);
        var run = Prepare(store);
        store.CompleteLevel(run.Id, run.EntryState, new() { IsComplete = true, Progress = 120 });
        var completed = store.Load(run.Id)!;
        var late = LivingArcadeHeroBalance.Copy(run.EntryState);
        late.TotalKills = 100;
        late.WeaponKind = "smg";
        late.Companions.Clear();
        store.RecordCombatProgress(run.Id, late);
        Assert.AreEqual(completed.Revision, store.Load(run.Id)!.Revision);
        Assert.AreEqual("pistol", store.Load(run.Id)!.PendingHero!.WeaponKind);
        Assert.IsTrue(store.Delete(run.Id));
        store.RecordCombatProgress(run.Id, late);
        Assert.IsNull(store.Load(run.Id));
        Assert.IsFalse(Directory.Exists(Path.Combine(_root, run.Id)));
    }
}
