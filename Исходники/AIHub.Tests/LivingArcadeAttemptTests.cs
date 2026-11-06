using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services.LivingArcade;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AIHub.Tests;

[TestClass]
public sealed class LivingArcadeAttemptTests
{
    private string _root = null!;
    [TestInitialize] public void Setup() => _root = Path.Combine(Path.GetTempPath(), "lopata-arcade-attempt-" + Guid.NewGuid().ToString("N"));
    [TestCleanup] public void Cleanup()
    {
        var path = Path.GetFullPath(_root);
        if (!path.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(path).StartsWith("lopata-arcade-attempt-", StringComparison.Ordinal)) throw new InvalidOperationException();
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }

    [TestMethod]
    public void AttemptSequenceIsDurableAndDoesNotMoveTheAuthoredTeachingBonuses()
    {
        var store = new LivingArcadeRunStore(_root); var run = store.Create("Run", 31);
        Assert.ThrowsExactly<InvalidOperationException>(() => store.GetOrCreateAttempt(run.Id));
        store.TrySaveFirstMiniEvents(run.Id, [new() { At = 20, Kind = "fog", X = .5, Strength = 1 }]);
        var first = store.GetOrCreateAttempt(run.Id);
        var saved = new LivingArcadeRunStore(_root).GetOrCreateAttempt(run.Id);
        Assert.AreEqual(JsonSerializer.Serialize(first), JsonSerializer.Serialize(saved));
        Assert.AreEqual(4, first.MiniEvents.Count(e => e.Tutorial));
        CollectionAssert.AreEqual(new[] { 15d, 39d, 64d, 91d }, first.MiniEvents.Where(e => e.Tutorial).Select(e => e.At).ToArray());
        Assert.AreEqual("xorshift32-v1", saved.Algorithm); Assert.AreEqual("fixed60-v4", saved.SimulationVersion);
        Assert.IsTrue(File.Exists(Path.Combine(_root, run.Id, "attempts", "1-1.json")));
        Assert.IsTrue(File.Exists(Path.Combine(_root, run.Id, "attempts", "1-1-fixed60-v4.json")));
    }

    [TestMethod]
    public void DefeatChangesOnlyTheNextAttemptAndTheRandomizedEventsRemainValidated()
    {
        var store = new LivingArcadeRunStore(_root); var run = store.Create("Run", 31);
        store.TrySaveFirstMiniEvents(run.Id, [new() { At = 0, Kind = "fog", X = .08, Strength = .25 },
            new() { At = 120, Kind = "meteor", X = .92, Strength = 3 }]);
        var first = store.GetOrCreateAttempt(run.Id);
        store.RecordDefeat(run.Id, 40);
        var next = store.GetOrCreateAttempt(run.Id);
        Assert.AreNotEqual(first.Seed, next.Seed); Assert.AreEqual(2, next.Number);
        var loaded = store.Load(run.Id)!;
        LivingArcadeLevelPolicy.Validate(loaded.Levels[1] with { MiniEvents = next.MiniEvents });
        Assert.IsTrue(File.Exists(Path.Combine(_root, run.Id, "attempts", "1-1.json")));
        Assert.IsTrue(File.Exists(Path.Combine(_root, run.Id, "attempts", "1-2.json")));
        Assert.AreEqual(40, loaded.DeathProgress); Assert.IsFalse(string.IsNullOrEmpty(loaded.Penalty));
    }

    [TestMethod]
    [DataRow("fixed60-v1")]
    [DataRow("fixed60-v2")]
    [DataRow("fixed60-v3")]
    public void LegacyAttemptUpgradesOnceWithoutRerollingEventsOrOverwritingTheOriginalArchive(string oldVersion)
    {
        var store = new LivingArcadeRunStore(_root);
        var run = store.Create("Legacy", 31);
        store.TrySaveFirstMiniEvents(run.Id, [new() { At = 20, Kind = "fog", X = .5, Strength = 1 }]);
        var first = store.GetOrCreateAttempt(run.Id);
        var legacy = first with { SimulationVersion = oldVersion };
        var saved = store.Load(run.Id)!;
        saved.Attempt = legacy;
        var path = Path.Combine(_root, run.Id, "run.json");
        var archive = Path.Combine(_root, run.Id, "attempts", "1-1.json");
        File.WriteAllText(path, JsonSerializer.Serialize(saved, LivingArcadeLevelPolicy.JsonOptions));
        var legacyArchive = JsonSerializer.SerializeToUtf8Bytes(legacy, LivingArcadeLevelPolicy.JsonOptions);
        File.WriteAllBytes(archive, legacyArchive);
        // Reproduce a historical save, which could not already contain a v4 archive.
        File.Delete(Path.Combine(_root, run.Id, "attempts", "1-1-fixed60-v4.json"));

        var reopened = new LivingArcadeRunStore(_root);
        Assert.AreEqual(oldVersion, reopened.Load(run.Id)!.Attempt!.SimulationVersion);
        var upgraded = reopened.GetOrCreateAttempt(run.Id);
        Assert.AreEqual("fixed60-v4", upgraded.SimulationVersion);
        Assert.AreEqual(legacy.Seed, upgraded.Seed);
        Assert.AreEqual(legacy.Level, upgraded.Level);
        Assert.AreEqual(legacy.Number, upgraded.Number);
        Assert.AreEqual(JsonSerializer.Serialize(legacy.MiniEvents), JsonSerializer.Serialize(upgraded.MiniEvents));
        Assert.AreEqual(oldVersion, legacy.SimulationVersion);
        CollectionAssert.AreEqual(legacyArchive, File.ReadAllBytes(archive));
        var after = reopened.Load(run.Id)!;
        Assert.AreEqual(saved.Revision + 1, after.Revision);
        Assert.AreEqual("fixed60-v4", after.Attempt!.SimulationVersion);
        _ = reopened.GetOrCreateAttempt(run.Id);
        Assert.AreEqual(after.Revision, reopened.Load(run.Id)!.Revision,
            "An already upgraded attempt must not be written or rerolled again.");
        var oldVersionPath = Path.Combine(_root, run.Id, "attempts", $"1-1-{oldVersion}.json");
        var newVersionPath = Path.Combine(_root, run.Id, "attempts", "1-1-fixed60-v4.json");
        var oldVersionBytes = File.ReadAllBytes(oldVersionPath);
        var newVersionBytes = File.ReadAllBytes(newVersionPath);
        Assert.AreEqual(oldVersion, JsonSerializer.Deserialize<LivingArcadeAttempt>(oldVersionBytes,
            LivingArcadeLevelPolicy.JsonOptions)!.SimulationVersion);
        Assert.AreEqual("fixed60-v4", JsonSerializer.Deserialize<LivingArcadeAttempt>(newVersionBytes,
            LivingArcadeLevelPolicy.JsonOptions)!.SimulationVersion);
        reopened.RecordDefeat(run.Id, 40);
        Assert.IsNull(reopened.Load(run.Id)!.Attempt);
        CollectionAssert.AreEqual(legacyArchive, File.ReadAllBytes(archive));
        CollectionAssert.AreEqual(oldVersionBytes, File.ReadAllBytes(oldVersionPath));
        CollectionAssert.AreEqual(newVersionBytes, File.ReadAllBytes(newVersionPath));
    }

    [TestMethod]
    public void CompletingAnAttemptRetainsBothCompatibilityAndVersionedRecords()
    {
        var store = new LivingArcadeRunStore(_root);
        var run = store.Create("Completed", 31);
        store.TrySaveFirstMiniEvents(run.Id, [new() { At = 20, Kind = "fog" }]);
        var attempt = store.GetOrCreateAttempt(run.Id);
        var folder = Path.Combine(_root, run.Id, "attempts");
        var original = File.ReadAllBytes(Path.Combine(folder, "1-1.json"));
        var versioned = File.ReadAllBytes(Path.Combine(folder, "1-1-fixed60-v4.json"));
        store.CompleteLevel(run.Id, new(), new() { IsComplete = true, Progress = 120 });
        Assert.IsNotNull(new LivingArcadeRunStore(_root).Load(run.Id)!.PendingHero);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(Path.Combine(folder, "1-1.json")));
        CollectionAssert.AreEqual(versioned, File.ReadAllBytes(Path.Combine(folder, "1-1-fixed60-v4.json")));
        Assert.AreEqual(attempt.Seed, JsonSerializer.Deserialize<LivingArcadeAttempt>(versioned,
            LivingArcadeLevelPolicy.JsonOptions)!.Seed);
    }
}
