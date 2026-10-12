using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;
using Button = System.Windows.Controls.Button;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicPoetryProfileTests
{
    [TestMethod]
    public void SixProfilesAreOrderedAndExcludedModelsCannotReappear()
    {
        var profiles = MusicPoetryProfileCatalog.All;
        Assert.AreEqual(6, profiles.Count);
        Assert.IsNull(MusicPoetryProfileCatalog.Find("profile-lyrical"));
        Assert.IsNull(MusicPoetryProfileCatalog.Find("profile-key2poetry"));
        Assert.IsNull(MusicPoetryModelCatalog.Find(new() { Path = "key2poetry/pytorch_model.bin", Format = "transformers" }));
        Assert.IsFalse(MusicPoetryModelCatalog.IsCompatible(new() { Path = "key2poetry/pytorch_model.bin", Format = "transformers" }));
        var sizes = profiles.Select(p => p.Files.Sum(f => f.SizeBytes)).ToArray();
        CollectionAssert.AreEqual(sizes.Order().ToArray(), sizes);
        var candidates = MusicPoetryModelCatalog.All.Where(c => c.DownloadBytes > 0).ToArray();
        Assert.AreEqual(6, candidates.Length);
        var license = new ComponentLicenseService(Path.Combine(AppContext.BaseDirectory, "Licenses"), "unused.json");
        foreach (var p in profiles)
        {
            var card = MusicPoetryProfileCatalog.Card(p, Path.Combine(Path.GetTempPath(), "poetry-root"));
            Assert.IsTrue(card.IsManaged && card.SupportsDirectDownload);
            Assert.IsFalse(card.IsPinned);
            Assert.IsTrue(card.Consumers.Any(c => c.Id == "music-poetry"));
            Assert.AreEqual(p.Revision, card.Revision);
            Assert.IsTrue(card.Files.All(f => f.SourceUrl.Contains("/resolve/" + p.Revision + "/") && f.Sha256.Length == 64));
            Assert.IsTrue(license.Entries.Any(entry => entry.Id == p.ArtifactId));
        }
        Assert.IsTrue(MusicPoetryModelCatalog.All.Where(c => c.DownloadBytes == 0 && c.Id != "core").All(c => c.InstalledOnly));
        Assert.IsFalse(MusicPoetryModelCatalog.All.Single(c => c.Id == "core").InstalledOnly);
        var emptySettings = new StorageSettings();
        emptySettings.Models.Locations.Clear();
        var visible = MusicPoetryModelCatalog.Choices(emptySettings);
        Assert.AreEqual(6, visible.Count(c => c.Candidate.DownloadBytes > 0));
    }

    [TestMethod]
    public void Gemma4PlannerUsesConservativeFullContextDimensions()
    {
        var metadata = new LiteraryModelMemoryMetadata("gemma4", 60, 262144, 0, 18687061504)
        { EmbeddingLength = 5376, HeadCount = 32, KvHeadCount = 16, KeyLength = 512, ValueLength = 512,
            KeyLengthSwa = 256, ValueLengthSwa = 256 };
        Assert.AreEqual(32768L * 60 * 16 * (512 + 512) * 2, LlamaDenseMemoryPolicy.KvBytes(metadata, 32768));
    }

    [TestMethod]
    public Task DownloadButtonBecomesSelectionWithoutRunningModel() => ScenarioNavigationTests.Sta(() =>
    {
        var candidate = MusicPoetryModelCatalog.All.Single(c => c.Id == "profile-katarau");
        var root = Path.Combine(Path.GetTempPath(), "lopata-poetry-profile-" + Guid.NewGuid().ToString("N"));
        var preferences = new MusicPoetryPreferences(Path.Combine(root, "prefs.json"));
        var result = new DebugModelInfo { Name = candidate.DisplayName!, Path = candidate.FileName, Format = "gguf" };
        var downloads = 0; DebugModelInfo? selected = null;
        var picker = new MusicPoetryModelWindow([new(candidate, null)], null, key => key, m => selected = m, preferences,
            (c, _, _) => { downloads++; Assert.AreSame(candidate, c); return Task.FromResult(result); });
        try
        {
            var buttons = ScenarioNavigationTests.LogicalDescendants(picker).OfType<Button>()
                .ToDictionary(b => AutomationProperties.GetAutomationId(b));
            var select = buttons["Music.Poetry.SelectModel.profile-katarau"];
            var download = buttons["Music.Poetry.SelectModel.Download.profile-katarau"];
            Assert.IsFalse(select.IsEnabled);
            Assert.AreEqual(0, downloads);
            download.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.AreEqual(1, downloads); Assert.IsFalse(picker.IsDownloading);
            Assert.IsTrue(select.IsEnabled); Assert.AreEqual(Visibility.Collapsed, download.Visibility);
            select.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.AreSame(result, selected);
        }
        finally { picker.Close(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    });
}
