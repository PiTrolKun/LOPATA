using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;
using Button = System.Windows.Controls.Button;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicModelRemovalTests
{
    [TestMethod]
    [DataRow(MusicStudioRuntime.Variation)]
    [DataRow(MusicAceCatalog.Variation)]
    [DataRow(MusicDiffRhythmCatalog.Variation)]
    [DataRow(MusicHeartMuLaCatalog.Variation)]
    public void RealCatalogEachConnectedFamilyRemovesItsCompletePackageInTemporaryStorage(string variation)
    {
        using var fixture = new Fixture();
        var cards = MusicModelVariants.Cards(fixture.Models, variation);
        foreach (var card in cards) {
            Fixture.Write(Path.Combine(card.InstallDirectory, card.Files[0].RelativePath));
            fixture.Store.Upsert(card);
        }
        // Real catalog paths, but never the actual user caches or installed model directory.
        var service = new MusicModelRemovalService(fixture.Store, new NullModelUsageGuard(), cacheDirectories: _ => []);
        var plan = service.Preview(variation, [fixture.Models]);
        Assert.AreEqual(cards.Count, plan.Components.Count);
        Assert.AreEqual(cards.Count * 3L, service.Remove(plan));
        foreach (var card in cards) {
            Assert.IsFalse(File.Exists(Path.Combine(card.InstallDirectory, card.Files[0].RelativePath)));
            Assert.AreEqual(ManagedModelStatuses.FilesRemoved, fixture.Store.Load(card.ModelArtifactId)!.Status);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "lopata-remove-test-" + Guid.NewGuid().ToString("N"));
        public string Models => Path.Combine(Root, "models");
        public string Cache => Path.Combine(Root, "cache", "revision");
        public ManagedModelLibraryStore Store { get; }
        public List<ManagedModelArtifactCard> Cards { get; } = [];
        public bool Active;
        public Fixture() { Store = new(Path.Combine(Root, "library")); }
        public ManagedModelArtifactCard Add(string id, string relative = "weights.bin")
        {
            var card = new ManagedModelArtifactCard { ModelArtifactId = id, DisplayName = id, RepositoryId = "test/" + id,
                Revision = id, ModelsRoot = Models, InstallDirectory = Path.Combine(Models, "Music", id, "revision"),
                IsManaged = true, CanRemoveFiles = true, Status = ManagedModelStatuses.Installed,
                LastVerifiedAt = DateTimeOffset.Now, RuntimeVerifiedAt = DateTimeOffset.Now,
                Files = [new() { RelativePath = relative, SizeBytes = 3, VerifiedSizeBytes = 3, VerifiedLastWriteTimeUtc = DateTimeOffset.Now }] };
            Write(Path.Combine(card.InstallDirectory, relative)); Cards.Add(card); Store.Upsert(card); return card;
        }
        public static void Write(string path) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, [1, 2, 3]); }
        public MusicModelRemovalService Service() => new(Store, new DelegateModelUsageGuard(_ => Active),
            (_, _) => Cards, _ => [Cache]);
        public MusicRemovalPlan Plan() => Service().Preview(MusicHeartMuLaCatalog.Variation, [Models]);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    [TestMethod]
    public void FullPackageRemovesExclusiveCachePartialsAndReceiptsKeepsUnknownAndUserFiles()
    {
        using var f = new Fixture(); var weights = f.Add("model"); f.Add("companion", "nested/decoder.bin");
        f.Add("libraries");
        var path = Path.Combine(weights.InstallDirectory, weights.Files[0].RelativePath);
        Fixture.Write(path + ".part"); Fixture.Write(path + ".part.segment.0");
        var unknown = Path.Combine(weights.InstallDirectory, "keep-user.txt"); Fixture.Write(unknown);
        Fixture.Write(Path.Combine(f.Cache, "nested", "runtime.py"));
        var stage = f.Cache + "." + Guid.NewGuid().ToString("N") + ".partial";
        Fixture.Write(Path.Combine(stage, "unfinished.py"));
        var project = Path.Combine(f.Root, "Projects", "song.opus"); Fixture.Write(project);
        var service = f.Service(); var plan = f.Plan(); Assert.AreEqual(21L, plan.Bytes);
        Assert.AreEqual(plan.Bytes, service.Remove(plan));
        Assert.IsFalse(File.Exists(path)); Assert.IsFalse(Directory.Exists(f.Cache)); Assert.IsFalse(Directory.Exists(stage));
        Assert.IsTrue(File.Exists(unknown)); Assert.IsTrue(File.Exists(project));
        var persisted = f.Store.Load("model")!;
        Assert.AreEqual(ManagedModelStatuses.FilesRemoved, persisted.Status);
        Assert.IsNull(persisted.LastVerifiedAt); Assert.IsNull(persisted.RuntimeVerifiedAt);
        Assert.AreEqual(0L, persisted.Files[0].VerifiedSizeBytes); Assert.IsNull(persisted.Files[0].VerifiedLastWriteTimeUtc);
        Assert.IsFalse(service.HasFiles(MusicHeartMuLaCatalog.Variation, [f.Models]));
    }

    [TestMethod]
    public void SharedComponentAndItsCacheRemainAvailableForOtherModel()
    {
        using var f = new Fixture(); var own = f.Add("model"); var shared = f.Add("companion");
        var other = new ManagedModelArtifactCard { ModelArtifactId = "other-model", RepositoryId = "test/other", Revision = "other",
            InstallDirectory = shared.InstallDirectory, ModelsRoot = f.Models, Files = [new() { RelativePath = "weights.bin" }] };
        f.Store.Upsert(other); Fixture.Write(Path.Combine(f.Cache, "keep.py"));
        var plan = f.Plan(); Assert.IsTrue(plan.Preserved.Contains("companion")); f.Service().Remove(plan);
        Assert.IsFalse(File.Exists(Path.Combine(own.InstallDirectory, "weights.bin")));
        Assert.IsTrue(File.Exists(Path.Combine(shared.InstallDirectory, "weights.bin")));
        Assert.IsTrue(Directory.Exists(f.Cache)); Assert.AreEqual(ManagedModelStatuses.Installed, f.Store.Load("companion")!.Status);
    }

    [TestMethod]
    public void ActivePinnedAndProtectedPackagesBlockBeforeAnyDeletion()
    {
        using var f = new Fixture(); var card = f.Add("model"); var path = Path.Combine(card.InstallDirectory, "weights.bin");
        f.Active = true; Assert.Throws<InvalidOperationException>(() => f.Plan()); f.Active = false;
        card.IsPinned = true; f.Store.Upsert(card); Assert.Throws<InvalidOperationException>(() => f.Plan());
        card.IsPinned = false; card.IsSystem = true; f.Store.Upsert(card); Assert.Throws<InvalidOperationException>(() => f.Plan());
        Assert.IsTrue(File.Exists(path));
    }

    [TestMethod]
    public void LaterUnsafeManifestCannotDeleteEarlierValidFiles()
    {
        using var f = new Fixture(); var good = f.Add("good"); var bad = f.Add("bad");
        bad.Files[0].RelativePath = "../outside.bin";
        Assert.Throws<InvalidDataException>(() => f.Plan());
        Assert.IsTrue(File.Exists(Path.Combine(good.InstallDirectory, "weights.bin")));
        bad.Files[0].RelativePath = "weights.bin:alternate"; Assert.Throws<InvalidDataException>(() => f.Plan());
        bad.InstallDirectory = f.Root; Assert.Throws<InvalidDataException>(() => f.Plan());
    }

    [TestMethod]
    public void ChangedInventoryOrPinAfterConfirmationBlocksAllDeletion()
    {
        using var f = new Fixture(); var card = f.Add("model"); var plan = f.Plan();
        Fixture.Write(Path.Combine(f.Cache, "new.py"));
        Assert.Throws<InvalidOperationException>(() => f.Service().Remove(plan));
        plan = f.Plan(); card.IsPinned = true; f.Store.Upsert(card);
        Assert.Throws<InvalidOperationException>(() => f.Service().Remove(plan));
        Assert.IsTrue(File.Exists(Path.Combine(card.InstallDirectory, "weights.bin")));
    }

    [TestMethod]
    public void FileLockLeavesNeedsVerificationInsteadOfReadyReceipt()
    {
        using var f = new Fixture(); var card = f.Add("model"); var plan = f.Plan();
        using var held = new FileStream(plan.Files[0].Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.Throws<IOException>(() => f.Service().Remove(plan));
        Assert.AreEqual(ManagedModelStatuses.NeedsVerification, f.Store.Load(card.ModelArtifactId)!.Status);
        Assert.IsNull(f.Store.Load(card.ModelArtifactId)!.RuntimeVerifiedAt);
    }

    [TestMethod]
    public void NestedDirectoryLinkIsRejectedBeforeDeletingModelOrTarget()
    {
        using var f = new Fixture(); var card = f.Add("model");
        var outside = Path.Combine(f.Root, "foreign"); Fixture.Write(Path.Combine(outside, "secret.bin"));
        var link = Path.Combine(card.InstallDirectory, "linked");
        // Junctions work without Developer Mode or administrator privileges on Windows.
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe") {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { "-NoProfile", "-Command", $"New-Item -ItemType Junction -Path '{link}' -Target '{outside}' | Out-Null" }
        })!;
        process.WaitForExit(); Assert.AreEqual(0, process.ExitCode, process.StandardError.ReadToEnd());
        try {
            card.Files.Add(new() { RelativePath = "linked/secret.bin" });
            Assert.Throws<InvalidDataException>(() => f.Plan());
            Assert.IsTrue(File.Exists(Path.Combine(card.InstallDirectory, "weights.bin")));
            Assert.IsTrue(File.Exists(Path.Combine(outside, "secret.bin")));
        }
        finally { Directory.Delete(link); }
    }

    [TestMethod]
    public async Task AllCardsHaveIconAtBottomLeftAndPlannedModelsCannotRemove()
    {
        await ScenarioNavigationTests.Sta(() => {
            foreach (var language in new[] { "ru", "en" }) {
                var l = new LocalizationService(); l.Load(language);
                using var cards = new MusicModelSelectionControl { CanRemoveModel = _ => true };
                cards.Localize(l.T);
                var icons = ScenarioNavigationTests.LogicalDescendants(cards).OfType<Button>()
                    .Where(b => AutomationProperties.GetAutomationId(b).StartsWith("Music.Models.Remove.")).ToArray();
                Assert.AreEqual(MusicModelSelectionCatalog.All.Count, icons.Length);
                foreach (var icon in icons) {
                    Assert.IsInstanceOfType<Viewbox>(icon.Content);
                    Assert.AreEqual(HorizontalAlignment.Left, icon.HorizontalAlignment); Assert.AreEqual(1, Grid.GetRow(icon));
                    var id = AutomationProperties.GetAutomationId(icon)["Music.Models.Remove.".Length..];
                    Assert.AreEqual(MusicModelSelectionCatalog.All.Single(c => c.Id == id).Variants.Any(v => v.Connected), icon.IsEnabled);
                    Assert.IsFalse(AutomationProperties.GetName(icon).StartsWith("Music."));
                }
                cards.CanApplyExample = () => false; cards.Localize(l.T);
                Assert.IsFalse(ScenarioNavigationTests.LogicalDescendants(cards).OfType<Button>()
                    .Where(b => AutomationProperties.GetAutomationId(b).StartsWith("Music.Models.Remove.")).Any(b => b.IsEnabled));
                Assert.AreNotEqual("Cloud.Tag.music_model_remove", l.T("Cloud.Tag.music_model_remove"));
            }
        });
    }
}
