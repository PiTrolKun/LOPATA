using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class LiteraryProjectStoreTests
{
    private string _root = null!;
    private LiteraryProjectStore _store = null!;
    [TestInitialize] public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), "AIHubTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _store = new LiteraryProjectStore(Path.Combine(_root, "index", "projects.json"));
    }
    [TestCleanup] public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private static LiteraryProject Draft(string name) => new()
    {
        ProjectName = name, WorkTitle = "Без названия", Author = "Автор", LanguageCode = "ru",
        Genres = ["fantasy", "drama"], CustomGenres = "Мой жанр", Form = "novella", Premise = "Замысел",
        Include = "Дружба", Avoid = "Жестокость", BasedOnExistingWorld = true, WorldSource = "Мир",
        CultureCountries = ["RU", "JP"], CultureNotes = "Фольклор и стоицизм"
    };

    [TestMethod] public void RoundTrip_PreservesMetadataMaterialsAndActiveChoice()
    {
        Directory.CreateDirectory(Path.Combine(_root, "a")); Directory.CreateDirectory(Path.Combine(_root, "b"));
        var one = Path.Combine(_root, "a", "мир.txt"); var two = Path.Combine(_root, "b", "мир.txt");
        File.WriteAllText(one, "Первый"); File.WriteAllText(two, "Второй");
        var entry = _store.Create(_root, Draft("Мой проект"), [one, two]);
        var saved = LiteraryProjectStore.ReadProject(entry.ProjectPath);
        Assert.AreEqual("Автор", saved.Author); Assert.AreEqual("ru", saved.LanguageCode);
        Assert.AreEqual("Без названия", saved.WorkTitle); Assert.AreEqual("novella", saved.Form);
        Assert.AreEqual("Замысел", saved.Premise); Assert.AreEqual("Дружба", saved.Include); Assert.AreEqual("Жестокость", saved.Avoid);
        CollectionAssert.AreEqual(new[] { "RU", "JP" }, saved.CultureCountries);
        CollectionAssert.AreEqual(new[] { "fantasy", "drama" }, saved.Genres);
        Assert.AreEqual("Мой жанр", saved.CustomGenres); Assert.AreEqual("Мир", saved.WorldSource);
        Assert.AreEqual("Фольклор и стоицизм", saved.CultureNotes);
        Assert.AreEqual(2, saved.Materials.Count);
        Assert.AreEqual("Первый", File.ReadAllText(Path.Combine(entry.ProjectPath, saved.Materials[0])));
        Assert.AreEqual("Второй", File.ReadAllText(Path.Combine(entry.ProjectPath, saved.Materials[1])));
        Assert.AreEqual("Первый", File.ReadAllText(one));
        Assert.IsNull(_store.Load().ActiveId);
        _store.SetActive(entry.Id);
        var other = _store.Create(_root, Draft("Другой"), []);
        Assert.AreEqual(entry.Id, _store.Load().ActiveId);
        _store.SetActive(other.Id);
        Assert.AreEqual(other.Id, new LiteraryProjectStore(Path.Combine(_root, "index", "projects.json")).Load().ActiveId);
    }

    [TestMethod] public void ExistingFolderAndInvalidNamesNeverOverwriteData()
    {
        var existing = Path.Combine(_root, "Existing"); Directory.CreateDirectory(existing);
        File.WriteAllText(Path.Combine(existing, "keep.txt"), "keep");
        Assert.Throws<IOException>(() => _store.Create(_root, Draft("Existing"), []));
        Assert.AreEqual("keep", File.ReadAllText(Path.Combine(existing, "keep.txt")));
        foreach (var name in new[] { "..", "../escape", "CON", "con.txt", "LPT1", "a.", "a/b", "" })
            Assert.IsFalse(LiteraryProjectStore.IsValidProjectName(name), name);
        Assert.IsTrue(LiteraryProjectStore.IsValidProjectName("Мой проект — 2"));
    }

    [TestMethod] public void FailedMaterialCopyLeavesNoProjectOrIndexEntry()
    {
        Assert.Throws<FileNotFoundException>(() => _store.Create(_root, Draft("Failed"), [Path.Combine(_root, "missing.txt")]));
        Assert.IsFalse(Directory.Exists(Path.Combine(_root, "Failed")));
        Assert.AreEqual(0, _store.Load().Projects.Count);
        Assert.AreEqual(0, Directory.GetDirectories(_root, ".lopata-new-*").Length);
    }

    [TestMethod] public void CorruptIndexIsNotReplacedByEmptyIndex()
    {
        Directory.CreateDirectory(Path.Combine(_root, "index"));
        var path = Path.Combine(_root, "index", "projects.json"); File.WriteAllText(path, "broken");
        Assert.Throws<System.Text.Json.JsonException>(() => _store.Create(_root, Draft("New"), []));
        Assert.AreEqual("broken", File.ReadAllText(path));
        Assert.IsFalse(Directory.Exists(Path.Combine(_root, "New")));
    }
}
