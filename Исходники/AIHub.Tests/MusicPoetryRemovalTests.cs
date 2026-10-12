using System.IO;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;
using Button = System.Windows.Controls.Button;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicPoetryRemovalTests
{
    private string _root = null!;
    [TestInitialize] public void Initialize() => _root = Path.Combine(Path.GetTempPath(), "lopata-profile-removal-" + Guid.NewGuid().ToString("N"));
    [TestCleanup] public void Cleanup()
    {
        var path = Path.GetFullPath(_root);
        if (!path.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(path).StartsWith("lopata-profile-removal-", StringComparison.Ordinal)) throw new InvalidOperationException();
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }
    private static MusicPoetryModelCandidate Candidate => MusicPoetryModelCatalog.All.Single(c => c.Id == "profile-katarau");
    private StorageSettings Settings(params string[] roots)
    {
        var settings = new StorageSettings(); settings.Models.Locations.Clear();
        foreach (var root in roots) settings.Models.Locations.Add(new() { Path = root });
        return settings;
    }
    private ManagedModelArtifactCard Install(string root)
    {
        var card = MusicPoetryProfileCatalog.Card(MusicPoetryProfileCatalog.Find(Candidate.Id)!, root);
        Directory.CreateDirectory(card.InstallDirectory);
        File.WriteAllText(Path.Combine(card.InstallDirectory, card.Files.Single().RelativePath), "fake test weights");
        return card;
    }

    [TestMethod]
    public void RemovalDeletesOnlyProfileAndPartialsAcrossRootsAndInvalidatesReceipts()
    {
        var root1 = Path.Combine(_root, "models1"); var root2 = Path.Combine(_root, "models2");
        var card = Install(root1); var card2 = Install(root2);
        var known = Path.Combine(card.InstallDirectory, card.Files.Single().RelativePath);
        File.WriteAllText(known + ".part", "partial");
        var unknown = Path.Combine(card.InstallDirectory, "user-note.txt"); File.WriteAllText(unknown, "keep");
        var inherited = Path.Combine(root1, "Qwen3-8B-Q4_K_M.gguf"); File.WriteAllText(inherited, "keep core");
        var store = new ManagedModelLibraryStore(Path.Combine(_root, "library"));
        card.LastVerifiedAt = DateTimeOffset.UtcNow; card.RuntimeVerifiedAt = DateTimeOffset.UtcNow;
        card.Status = ManagedModelStatuses.Installed; store.Upsert(card);
        var settings = Settings(root1, root2);
        Assert.IsTrue(MusicPoetryProfileRemoval.HasFiles(Candidate, settings));
        Assert.IsTrue(new MusicPoetryProfileRemoval(store, new NullModelUsageGuard()).Remove(Candidate, settings) > 0);
        Assert.IsFalse(File.Exists(known)); Assert.IsFalse(File.Exists(known + ".part"));
        Assert.IsFalse(Directory.Exists(card2.InstallDirectory));
        Assert.AreEqual("keep", File.ReadAllText(unknown)); Assert.AreEqual("keep core", File.ReadAllText(inherited));
        var removed = store.Load(card.ModelArtifactId)!;
        Assert.AreEqual(ManagedModelStatuses.FilesRemoved, removed.Status);
        Assert.IsNull(removed.LastVerifiedAt); Assert.IsNull(removed.RuntimeVerifiedAt);
        Assert.AreEqual(0L, removed.StoredBytes);
        Assert.IsFalse(MusicPoetryProfileRemoval.HasFiles(Candidate, settings));
    }

    [TestMethod]
    public void ActivePinnedAndSharedProfilesCannotBeDeleted()
    {
        var root = Path.Combine(_root, "models"); var card = Install(root);
        var settings = Settings(root); var path = Path.Combine(card.InstallDirectory, card.Files.Single().RelativePath);
        var store = new ManagedModelLibraryStore(Path.Combine(_root, "library"));
        store.Upsert(card);
        Assert.Throws<InvalidOperationException>(() => new MusicPoetryProfileRemoval(store, new DelegateModelUsageGuard(_ => true)).Remove(Candidate, settings));
        card.IsPinned = true; store.Upsert(card);
        Assert.Throws<InvalidOperationException>(() => new MusicPoetryProfileRemoval(store, new NullModelUsageGuard()).Remove(Candidate, settings));
        card.IsPinned = false; card.Consumers.Add(new() { Id = "another-scenario" }); store.Upsert(card);
        Assert.Throws<InvalidOperationException>(() => new MusicPoetryProfileRemoval(store, new NullModelUsageGuard()).Remove(Candidate, settings));
        Assert.IsTrue(File.Exists(path));
    }

    [TestMethod]
    public void InheritedModelsAreRejectedBeforeAnyFilesystemMutation()
    {
        var store = new ManagedModelLibraryStore(Path.Combine(_root, "library"));
        var service = new MusicPoetryProfileRemoval(store, new NullModelUsageGuard());
        foreach (var candidate in MusicPoetryModelCatalog.All.Where(c => c.DownloadBytes == 0))
        {
            Assert.IsFalse(MusicPoetryProfileRemoval.HasFiles(candidate, Settings(_root)));
            Assert.Throws<InvalidOperationException>(() => service.Remove(candidate, Settings(_root)));
        }
        Assert.IsFalse(Directory.Exists(_root));
    }

    [TestMethod]
    public Task PickerOffersRemovalOnlyForProfilesAndShowsRealDownloadSize() => ScenarioNavigationTests.Sta(() =>
    {
        var root = Path.Combine(_root, "models");
        var choices = MusicPoetryModelCatalog.All.Select(c => new MusicPoetryModelChoice(c,
            new DebugModelInfo { Name = c.FileName, Path = c.FileName, Format = "gguf" })).ToArray();
        var localization = new LocalizationService(); localization.Load("en");
        var window = new MusicPoetryModelWindow(choices, null, localization.T, _ => { },
            new MusicPoetryPreferences(Path.Combine(root, "prefs.json")), remove: _ => 0, hasFiles: _ => true);
        try
        {
            var buttons = ScenarioNavigationTests.LogicalDescendants(window).OfType<Button>()
                .Select(AutomationProperties.GetAutomationId).Where(id => id.StartsWith("Music.Poetry.SelectModel.Remove.")).ToArray();
            Assert.AreEqual(6, buttons.Length);
            Assert.IsTrue(buttons.All(id => id.Contains(".profile-")));
            var texts = ScenarioNavigationTests.LogicalDescendants(window).OfType<TextBlock>().Select(t => t.Text).ToArray();
            Assert.IsTrue(texts.Contains("Download size: " + MusicPoetryModelWindow.FormatDownloadSize(Candidate.DownloadBytes)));
        }
        finally { window.Close(); }
    });
}
