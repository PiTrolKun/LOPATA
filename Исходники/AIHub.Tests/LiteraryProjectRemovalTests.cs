using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class LiteraryProjectRemovalTests
{
    private string _root = null!, _index = null!;
    private LiteraryProjectStore _store = null!;
    [TestInitialize] public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), "AIHubTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _index = Path.Combine(_root, "index", "projects.json");
        _store = new(_index);
    }
    [TestCleanup] public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
    private LiteraryProjectEntry Create(string name, params string[] materials) => _store.Create(_root,
        new LiteraryProject { ProjectName = name, Genres = ["fantasy"], BasedOnExistingWorld = true }, materials);

    [TestMethod] public void KeepFiles_RemovesRegistrationAndActiveChoiceButPreservesEveryFile()
    {
        var entry = Create("Мой проект"); _store.SetActive(entry.Id);
        File.WriteAllText(Path.Combine(entry.ProjectPath, "draft.txt"), "Набросок");
        var before = Directory.GetFiles(entry.ProjectPath).ToDictionary(p => p, File.ReadAllText);
        _store.Remove(entry, LiteraryProjectRemoval.KeepFiles);
        Assert.IsNull(_store.Load().ActiveId); Assert.AreEqual(0, _store.Load().Projects.Count);
        foreach (var pair in before) Assert.AreEqual(pair.Value, File.ReadAllText(pair.Key));
    }

    [TestMethod] public void DeleteFiles_RemovesWholeProjectButPreservesOriginalAndNeighbour()
    {
        var source = Path.Combine(_root, "Книга.txt"); File.WriteAllText(source, "Оригинал");
        var entry = Create("Удаляемый", source); var neighbour = Create("Сосед"); _store.SetActive(neighbour.Id);
        Directory.CreateDirectory(Path.Combine(entry.ProjectPath, "chapters", "backups"));
        File.WriteAllText(Path.Combine(entry.ProjectPath, "chapters", "backups", "001.txt"), "Глава");
        _store.Remove(entry, LiteraryProjectRemoval.DeleteFiles);
        Assert.IsFalse(Directory.Exists(entry.ProjectPath)); Assert.AreEqual("Оригинал", File.ReadAllText(source));
        Assert.AreEqual(neighbour.Id, _store.Load().ActiveId);
        Assert.AreEqual(neighbour.Id, LiteraryProjectStore.ReadProject(neighbour.ProjectPath).Id);
        Assert.AreEqual(1, _store.Load().Projects.Count);
    }

    [TestMethod] public void WrongIdentityOrStaleLocation_LeavesFilesAndIndex()
    {
        var entry = Create("Проект"); var other = Create("Другой");
        Assert.Throws<IOException>(() => _store.Remove(entry with { ProjectPath = other.ProjectPath }, LiteraryProjectRemoval.DeleteFiles));
        File.Copy(Path.Combine(other.ProjectPath, "project.json"), Path.Combine(entry.ProjectPath, "project.json"), true);
        Assert.Throws<IOException>(() => _store.Remove(entry, LiteraryProjectRemoval.DeleteFiles));
        Assert.AreEqual(2, _store.Load().Projects.Count); Assert.IsTrue(Directory.Exists(entry.ProjectPath));
    }

    [TestMethod] public void MissingFolder_CanRemoveStaleEntry()
    {
        var entry = Create("Исчезнувший"); Directory.Delete(entry.ProjectPath, true);
        _store.SetActive(entry.Id); _store.Remove(entry, LiteraryProjectRemoval.DeleteFiles);
        Assert.AreEqual(0, _store.Load().Projects.Count); Assert.IsNull(_store.Load().ActiveId);
    }

    [TestMethod] public void LockedFile_PreventsPartialDeletionAndKeepsRegistration()
    {
        var entry = Create("Блокировка");
        var first = Path.Combine(entry.ProjectPath, "first.txt"); var locked = Path.Combine(entry.ProjectPath, "locked.txt");
        File.WriteAllText(first, "Первый"); File.WriteAllText(locked, "Занят");
        using var handle = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.Throws<IOException>(() => _store.Remove(entry, LiteraryProjectRemoval.DeleteFiles));
        Assert.AreEqual("Первый", File.ReadAllText(first)); Assert.AreEqual(1, _store.Load().Projects.Count);
    }

    [TestMethod] public void NestedRegisteredProject_IsNeverDeletedWithParent()
    {
        var parent = Create("Родитель");
        var child = _store.Create(parent.ProjectPath, new LiteraryProject { ProjectName = "Ребёнок", Genres = ["drama"] }, []);
        Assert.Throws<IOException>(() => _store.Remove(parent, LiteraryProjectRemoval.DeleteFiles));
        Assert.AreEqual(child.Id, LiteraryProjectStore.ReadProject(child.ProjectPath).Id);
        Assert.AreEqual(2, _store.Load().Projects.Count);
    }

    [TestMethod] public void UnsafeRootAndIndexDirectory_AreRejected()
    {
        var entry = Create("Проект");
        foreach (var unsafePath in new[] { Path.GetPathRoot(_root)!, _root })
        {
            var forged = entry with { ProjectPath = unsafePath };
            File.WriteAllText(_index, JsonSerializer.Serialize(new LiteraryProjectIndex { Projects = [forged] }));
            Assert.Throws<IOException>(() => _store.Remove(forged, LiteraryProjectRemoval.DeleteFiles));
            Assert.IsTrue(File.Exists(_index)); Assert.IsTrue(Directory.Exists(entry.ProjectPath));
        }
    }

    [TestMethod] public void CorruptManifest_CanBeForgottenButCannotBeDeleted()
    {
        var entry = Create("Поврежденный"); var manifest = Path.Combine(entry.ProjectPath, "project.json");
        File.WriteAllText(manifest, "broken");
        Assert.Throws<JsonException>(() => _store.Remove(entry, LiteraryProjectRemoval.DeleteFiles));
        _store.Remove(entry, LiteraryProjectRemoval.KeepFiles);
        Assert.AreEqual("broken", File.ReadAllText(manifest)); Assert.AreEqual(0, _store.Load().Projects.Count);
    }

    [TestMethod] public void JunctionToExternalMaterial_IsRejectedWithoutTouchingTarget()
    {
        var entry = Create("Со ссылкой"); var outside = Path.Combine(_root, "external");
        Directory.CreateDirectory(outside); File.WriteAllText(Path.Combine(outside, "book.txt"), "Original");
        var link = Path.Combine(entry.ProjectPath, "linked");
        // Windows directory junction creation needs no symbolic-link privilege. All paths are test-owned.
        using var command = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/d /c mklink /J \"{link}\" \"{outside}\"", UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        command.WaitForExit(); Assert.AreEqual(0, command.ExitCode, command.StandardError.ReadToEnd());
        try
        {
            Assert.Throws<IOException>(() => _store.Remove(entry, LiteraryProjectRemoval.DeleteFiles));
            Assert.AreEqual("Original", File.ReadAllText(Path.Combine(outside, "book.txt")));
            Assert.AreEqual(1, _store.Load().Projects.Count);
        }
        finally { Directory.Delete(link, recursive: false); }
    }
}
