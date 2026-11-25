using System.IO;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.SongClip;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AIHub.Tests;

[TestClass]
public sealed class ManagedWorkStorageTests
{
    [TestMethod]
    public void ExistingWorkSurvivesChangesToConfiguredRoots()
    {
        using var fixture = new VideoPreparationTests.TestFiles();
        var profile = fixture.File("profile");
        var first = fixture.File("результаты");
        Directory.CreateDirectory(first);
        var actual = first;
        var storage = new ManagedWorkStorage(profile, () => new() { Locations = [new() { Path = actual }] });
        var id = Guid.NewGuid().ToString("N");
        var saved = storage.Job("Video", id, outputDirectory: fixture.File("отдельная работа"));
        var scratch = storage.Job("Render", id, temporary: true);
        Assert.StartsWith(fixture.File("отдельная работа"), saved);
        Assert.StartsWith(Path.Combine(first, "AI_HUB", "Temporary"), scratch);
        actual = fixture.File("новый корень");
        Directory.CreateDirectory(actual);
        Assert.AreEqual(saved, storage.Job("Video", id));
        Assert.AreEqual(scratch, storage.Job("Render", id, temporary: true));
        Assert.StartsWith(actual, storage.Job("Video", Guid.NewGuid().ToString("N")));
    }

    [TestMethod]
    public void ActiveReservationsPreventConcurrentOverbooking()
    {
        using var fixture = new VideoPreparationTests.TestFiles();
        var path = fixture.File("results");
        var profile = fixture.File("profile");
        var settings = new StorageCategorySettings
        {
            Locations = [new() { Path = path, LimitGb = 0.1 }],
            TotalLimitGb = 0.1,
            AllowTemporaryOverflow = true,
            TemporaryOverflowGb = 0.05
        };
        using (WorkspaceCapacity.Reserve(profile, settings, path, 65 * 1024 * 1024, false))
        {
            Assert.ThrowsExactly<IOException>(() => WorkspaceCapacity.Reserve(profile, settings, path, 65 * 1024 * 1024, false));
            using var temporary = WorkspaceCapacity.Reserve(profile, settings, path, 65 * 1024 * 1024, true);
        }
        using var replacement = WorkspaceCapacity.Reserve(profile, settings, path, 65 * 1024 * 1024, false);
    }

    [TestMethod]
    public async Task ReferencesAreLinkedAndLegacyProjectMigrationPreservesBytes()
    {
        using var fixture = new VideoPreparationTests.TestFiles();
        var profile = fixture.File("old");
        var results = fixture.File("new");
        Directory.CreateDirectory(results);
        var storage = new ManagedWorkStorage(profile, () => new() { Locations = [new() { Path = results }] });
        using var activate = ManagedWorkStorage.Use(storage);
        var id = Guid.NewGuid().ToString("N");
        var legacy = new SongClipStore(Path.Combine(profile, "SongClip", "Projects"));
        var original = fixture.File("song.wav");
        File.WriteAllText(original, "test source");
        var attached = await legacy.ImportAsync(id, original, CancellationToken.None);
        legacy.Save(new SongClipProject { Id = id, Name = "test", Song = attached });
        var unrelated = fixture.File("leave.txt");
        File.WriteAllText(unrelated, "keep");
        var mover = new LegacyWorkTransfer(storage);
        var item = mover.Plan().Single();
        Assert.AreEqual(item.Bytes, await mover.MoveAsync(item, CancellationToken.None));
        Assert.AreEqual("keep", File.ReadAllText(unrelated));
        var store = new SongClipStore();
        Assert.AreEqual("test", store.Load(id).Name);
        Assert.AreEqual("test source", File.ReadAllText(store.Resolve(id, attached)));
        Assert.IsFalse(File.Exists(legacy.Resolve(id, attached)));
        Assert.IsEmpty(mover.Plan());
        var newId = Guid.NewGuid().ToString("N");
        var linked = await store.ImportAsync(newId, original, CancellationToken.None);
        Assert.AreEqual(original, store.Resolve(newId, linked));
        Assert.IsFalse(Directory.Exists(Path.Combine(store.Folder(newId), "attachments")));
        File.Move(original, fixture.File("moved.wav"));
        Assert.AreEqual(original, store.Resolve(newId, linked));
        Assert.IsFalse(File.Exists(store.Resolve(newId, linked)));
        await Lopata.Updates.LegacyWorkspaceMoves.RestoreAsync(profile);
        Assert.AreEqual("test", legacy.Load(id).Name);
        Assert.AreEqual("test source", File.ReadAllText(legacy.Resolve(id, attached)));
        Assert.IsFalse(File.Exists(Path.Combine(item.Destination, "project.json")));
        Assert.AreEqual("keep", File.ReadAllText(unrelated));
    }
}
