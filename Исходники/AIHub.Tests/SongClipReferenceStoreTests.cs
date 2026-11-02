using System.IO;
using System.Text.Json;
using AIHub.Services.SongClip;

namespace AIHub.Tests;

[TestClass]
public sealed class SongClipReferenceStoreTests
{
    [TestMethod]
    public void LegacyReferenceHasUnknownRoleAndSnapshotIsNeverRewritten()
    {
        using var files = new MusicProjectTests.Files(); var store = new SongClipStore(files.Root);
        var project = new SongClipProject { Reference = "attachments/actor.png" }; store.Save(project);
        var loaded = store.Load(project.Id);
        Assert.IsNull(loaded.References);
        Assert.AreEqual(new SongClipReference(project.Reference, "", ""), SongClipReferences.For(loaded).Single());
        var attempt = Attempt(project); store.EnsureAttemptSnapshot(attempt);
        var path = store.Resolve(project.Id, "attempts/" + attempt.Id + "/snapshot.json");
        var before = File.ReadAllBytes(path); var timestamp = File.GetLastWriteTimeUtc(path);
        Assert.IsFalse(JsonSerializer.Serialize(attempt).Contains("\"References\"", StringComparison.Ordinal));
        store.EnsureAttemptSnapshot(attempt);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(path)); Assert.AreEqual(timestamp, File.GetLastWriteTimeUtc(path));
    }

    [TestMethod]
    public void MultipleRolesRoundTripAndEquivalentArraysKeepTheImmutableSnapshot()
    {
        using var files = new MusicProjectTests.Files(); var store = new SongClipStore(files.Root);
        var project = new SongClipProject { Reference = "attachments/actor.png", References = [
            new("attachments/actor.png", "performer", "Она поёт песню в кадре."),
            new("attachments/home.png", "world", "В квартире происходят события."),
            new("attachments/bowl.png", "object", "Кот ищет эту миску.")] };
        store.Save(project); var loaded = store.Load(project.Id);
        Assert.IsTrue(SongClipReferences.Same(project.References, loaded.References));
        var attempt = Attempt(project) with { References = project.References.ToArray(), PlanningVersion = 1 };
        store.EnsureAttemptSnapshot(attempt);
        var path = store.Resolve(project.Id, "attempts/" + attempt.Id + "/snapshot.json");
        var before = File.ReadAllBytes(path);
        var reloaded = JsonSerializer.Deserialize<SongClipAttempt>(File.ReadAllText(path))!;
        Assert.AreNotSame(attempt.References, reloaded.References); store.EnsureAttemptSnapshot(reloaded);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
        var changed = reloaded.References!.ToArray(); changed[1] = changed[1] with { Description = "Другая роль." };
        Assert.Throws<InvalidDataException>(() => store.EnsureAttemptSnapshot(reloaded with { References = changed }));
        Assert.Throws<InvalidDataException>(() => store.EnsureAttemptSnapshot(reloaded with { References = reloaded.References!.Reverse().ToArray(), Reference = changed[^1].File }));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
    }

    [TestMethod]
    [DataRow("unknown-kind")]
    [DataRow("empty-description")]
    [DataRow("too-long")]
    [DataRow("duplicate")]
    [DataRow("primary-mismatch")]
    [DataRow("too-many")]
    public void NewAttemptsRequireValidPerFileRoles(string defect)
    {
        var project = new SongClipProject { Reference = "attachments/actor.png" };
        SongClipReference[] references = [new(project.Reference, "performer", "Певица исполняет песню.")];
        if (defect == "unknown-kind") references[0] = references[0] with { Kind = "" };
        if (defect == "empty-description") references[0] = references[0] with { Description = " " };
        if (defect == "too-long") references[0] = references[0] with { Description = new('x', 2001) };
        if (defect == "duplicate") references = [references[0], references[0] with { Kind = "world" }];
        if (defect == "primary-mismatch") references[0] = references[0] with { File = "attachments/other.png" };
        if (defect == "too-many") references = Enumerable.Range(0, 10).Select(i => references[0] with { File = $"attachments/{i}.png" }).ToArray();
        Assert.Throws<InvalidDataException>(() => SongClipReferences.ValidateNewAttempt(Attempt(project) with { References = references, PlanningVersion = 1 }));
    }

    [TestMethod]
    public async Task HashTracksEveryFileItsRoleAndOrderWhileLegacyHashStaysExact()
    {
        using var files = new MusicProjectTests.Files(); var store = new SongClipStore(files.Root);
        var project = new SongClipProject { Reference = "attachments/actor.png" };
        var first = store.Resolve(project.Id, project.Reference); var second = store.Resolve(project.Id, "attachments/world.png");
        Directory.CreateDirectory(Path.GetDirectoryName(first)!); File.WriteAllText(first, "actor"); File.WriteAllText(second, "world");
        var legacy = Attempt(project);
        Assert.AreEqual(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(first))),
            await SongClipReferences.HashAsync(legacy, store, CancellationToken.None));
        var attempt = legacy with { PlanningVersion = 1, References = [new(project.Reference, "performer", "Sings in the frame."), new("attachments/world.png", "world", "Events happen here.")] };
        var before = await SongClipReferences.HashAsync(attempt, store, CancellationToken.None);
        var changed = attempt.References.ToArray(); changed[1] = changed[1] with { Description = "A new place." };
        Assert.AreNotEqual(before, await SongClipReferences.HashAsync(attempt with { References = changed }, store, CancellationToken.None));
        changed = attempt.References.Reverse().ToArray();
        Assert.AreNotEqual(before, await SongClipReferences.HashAsync(attempt with { References = changed, Reference = changed[0].File }, store, CancellationToken.None));
        File.WriteAllText(second, "world changed");
        Assert.AreNotEqual(before, await SongClipReferences.HashAsync(attempt, store, CancellationToken.None));
    }

    [TestMethod]
    public void ReferenceOnlyDraftIsSavedAndPathEscapeIsRejected()
    {
        using var files = new MusicProjectTests.Files(); var store = new SongClipStore(files.Root);
        var project = new SongClipProject { Reference = "attachments/actor.png", References = [new("attachments/actor.png", "performer", "Sings.")] };
        Assert.IsTrue(store.SaveDraft(project)); Assert.AreEqual(project.Id, store.List().Single().Id);
        project.Reference = "../outside.png"; project.References = [new(project.Reference, "object", "Prop.")];
        Assert.Throws<InvalidDataException>(() => store.Save(project));
    }

    private static SongClipAttempt Attempt(SongClipProject project) => new(Guid.NewGuid().ToString("N"), project.Id,
        "attachments/song.wav", project.Reference, "Reference store test", "", "", new(), "models", 2);
}
