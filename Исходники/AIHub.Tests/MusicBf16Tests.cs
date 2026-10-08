using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicBf16Tests
{
    [TestMethod]
    public async Task Bf16PreparationRegistersEveryCardAndReachesDownloadConsent()
    {
        using var files = new MusicProjectTests.Files();
        var store = new ManagedModelLibraryStore(Path.Combine(files.Root, "library"));
        using var preparation = new MusicPreparationService(store) { Variation = MusicModelVariants.Bf16 };
        var models = Path.Combine(files.Root, "models");
        var previous = ComponentLicenseGate.ConfirmAsync;
        string[]? requested = null;
        ComponentLicenseGate.ConfirmAsync = (ids, _) => {
            requested = ids.ToArray(); throw new OperationCanceledException();
        };
        try
        {
            var cards = await preparation.CheckAsync(models, null, default);
            Assert.HasCount(3, cards);
            Assert.IsNull(requested);
            Assert.IsFalse(Directory.Exists(models));
            var licenses = new ComponentLicenseService(Path.Combine(AppContext.BaseDirectory, "Licenses"), Path.Combine(files.Root, "consent.json"));
            foreach (var card in cards)
            {
                Assert.IsNotNull(store.Load(card.ModelArtifactId));
                Assert.IsTrue(licenses.Entries.Any(e => e.Id == card.ModelArtifactId));
                Assert.AreNotEqual(ManagedModelStatuses.Installed, card.Status);
            }
            await Assert.ThrowsAsync<OperationCanceledException>(() => preparation.PrepareAsync(models, true, null, default));
            CollectionAssert.AreEquivalent(MusicModelVariants.Components(MusicModelVariants.Bf16).ToArray(), requested!);
            Assert.IsFalse(Directory.Exists(models));
            ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
            byte[] payload = [1, 3, 5, 7];
            using var client = new HttpClient(new Bf16PayloadHandler(payload));
            using var downloads = new ManagedModelAcquisitionService(store, client);
            foreach (var card in cards)
            {
                // Exercise each real identity through acquisition with tiny test-only files.
                foreach (var file in card.Files) {
                    file.SizeBytes = payload.Length;
                    file.Sha256 = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
                }
                store.Upsert(card);
                var installed = await downloads.DownloadAsync(card.ModelArtifactId, null, default);
                Assert.AreEqual(ManagedModelStatuses.Installed, installed.Status);
                foreach (var file in installed.Files)
                    CollectionAssert.AreEqual(payload, File.ReadAllBytes(Path.Combine(installed.InstallDirectory, file.RelativePath)));
            }
        }
        finally { ComponentLicenseGate.ConfirmAsync = previous; }
    }

    private sealed class Bf16PayloadHandler(byte[] payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
    }

    [TestMethod]
    public void OfficialRequestReceivesOneSeedExactSamplingAndCheckpoint()
    {
        var expert = MusicTuningProfile.Move(MusicModelVariants.Defaults(MusicModelVariants.Bf16), -.5, .7);
        expert.Values["steps"] = 48; expert.Values["cfg_scale"] = 1.3;
        var checkpoint = "{\"abc\":\"X:1\\nK:C\",\"abc_ids\":[151847, 52]}";
        var request = new MusicYueRequest("doom metal", "ЛАПАТА", 321, 321, 30) { Expert = expert, Abc = checkpoint };
        using var data = JsonDocument.Parse(JsonSerializer.Serialize(MusicBf16Worker.Request(request)));
        var root = data.RootElement;
        Assert.AreEqual(321, root.GetProperty("seed").GetInt32()); Assert.IsFalse(root.TryGetProperty("lm_seed", out _));
        Assert.IsFalse(root.TryGetProperty("peak_clip", out _)); Assert.AreEqual(checkpoint, root.GetProperty("abc").GetString());
        Assert.AreEqual("ЛАПАТА", root.GetProperty("lyrics").GetString()); Assert.AreEqual("doom metal", root.GetProperty("style").GetString());
        Assert.AreEqual(48, root.GetProperty("steps").GetInt32()); Assert.AreEqual(1.3, root.GetProperty("cfg_scale").GetDouble());
        Assert.AreEqual(expert.Get("abc_sampling.temperature"), root.GetProperty("abc_sampling").GetProperty("temperature").GetDouble());
        Assert.AreEqual(expert.Get("semantic_sampling.top_p"), root.GetProperty("semantic_sampling").GetProperty("top_p").GetDouble());
        Assert.AreEqual(750, root.GetProperty("semantic_sampling").GetProperty("max_tokens").GetInt32());
        var invalid = expert with { Values = null! }; Assert.Throws<InvalidDataException>(invalid.Validate);
    }
    [TestMethod]
    public void CheckpointTokenizerMatchesIndependentOrdinaryTokenVectors()
    {
        var path = Environment.GetEnvironmentVariable("LOPATA_BF16_TOKENIZER_PATH");
        if (string.IsNullOrEmpty(path)) Assert.Inconclusive("Set LOPATA_BF16_TOKENIZER_PATH to checkpoint qwen.tiktoken.");
        var tokenizer = new MusicBf16Tokenizer(path!);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Music", "tokenizer-reference.json")));
        foreach (var vector in json.RootElement.GetProperty("vectors").EnumerateArray()) {
            var text = vector.GetProperty("text").GetString()!;
            var expected = vector.GetProperty("ids").EnumerateArray().Select(v => v.GetInt32()).ToArray();
            CollectionAssert.AreEqual(expected, tokenizer.Encode(text), text[..Math.Min(text.Length, 80)]);
        }
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => tokenizer.Encode("Текст", cancel.Token));
    }
    [TestMethod]
    public async Task PinnedLibraryWheelsExtractTogetherWithoutCollisions()
    {
        var path = Environment.GetEnvironmentVariable("LOPATA_BF16_WHEELS_PATH");
        if (string.IsNullOrEmpty(path)) Assert.Inconclusive("Set LOPATA_BF16_WHEELS_PATH to the pinned library archives.");
        using var files = new MusicProjectTests.Files(); var target = Path.Combine(files.Root, "overlay"); Directory.CreateDirectory(target);
        var card = MusicModelVariants.Cards(files.Root, MusicModelVariants.Bf16)[2];
        foreach (var file in card.Files) {
            var artifact = new PinnedPythonArtifact("YuE2", "pinned", file.RelativePath, file.SizeBytes, file.Sha256, new(file.SourceUrl));
            await PythonWheelExtractor.ExtractAsync(artifact, Path.Combine(path!, file.RelativePath), target, default);
        }
        Assert.IsTrue(File.Exists(Path.Combine(target, "Lib", "site-packages", "yue2", "pipeline.py")));
        Assert.IsTrue(File.Exists(Path.Combine(target, "Lib", "site-packages", "transformers", "__init__.py")));
        Assert.IsFalse(Directory.Exists(Path.Combine(target, "Lib", "site-packages", "torch")));
    }
    [TestMethod]
    public void JobKeepsOwnModelSeedsAndMetadataWhenEditorChanges()
    {
        using var files = new MusicProjectTests.Files(); var jobs = new MusicGenerationJobs(files.Root);
        var settings = MusicTuningProfile.Move(MusicModelVariants.Defaults(MusicModelVariants.Bf16), -.7, .5);
        settings.Values["seed"] = 123;
        var job = jobs.Create(files.Root, files.Root, "BF16", 2, 60, "doom metal", "Текст", expert: settings);
        settings.Values["seed"] = 555;
        var saved = jobs.Load(job.Id);
        Assert.AreEqual(MusicModelVariants.Bf16, saved.Variation);
        Assert.AreEqual(MusicModelVariants.Bf16Revision, saved.ModelRevision);
        Assert.AreEqual(MusicModelVariants.VaeRevision, saved.DecoderRevision);
        Assert.AreEqual(123, saved.Variants[0].SoundSeed); Assert.AreEqual(124, saved.Variants[1].SoundSeed);
        Assert.AreEqual(saved.Variants[0].SoundSeed, saved.Variants[0].LanguageSeed);
        var tags = MusicSongMetadata.Create(saved, saved.Variants[0], 0);
        Assert.AreEqual("YuE2 3B BF16", tags["LOPATA_MODEL"]);
        Assert.DoesNotContain("peak_clip", tags["LOPATA_PARAMETERS"]);
        Assert.DoesNotContain("lm_seed", tags["LOPATA_PARAMETERS"]);
        Assert.AreEqual("Текст", tags["lyrics"]); Assert.IsFalse(tags.ContainsKey("LOPATA_GGML_REVISION"));
        Assert.Throws<InvalidDataException>(() => jobs.Save(saved with { ModelRevision = MusicComponentCatalog.Revision }));
    }
    [TestMethod]
    public void PresetsRequireExplicitTransferAndKeepCollectionsSeparate()
    {
        using var files = new MusicProjectTests.Files();
        var native = new MusicExpertSettings(); native.Values["lm_seed"] = 12; native.Values["peak_clip"] = 30;
        native.Values["seed"] = 987; native.Values["steps"] = 48;
        var transferred = MusicModelVariants.Transfer(native, MusicModelVariants.Bf16);
        Assert.AreEqual(987d, transferred.Get("seed")); Assert.AreEqual(48d, transferred.Get("steps"));
        Assert.AreEqual(-1d, transferred.Get("lm_seed")); Assert.AreEqual(10d, transferred.Get("peak_clip"));
        var q8 = new ModelExpertPresets(Path.Combine(files.Root, "q8"));
        var bf16 = new ModelExpertPresets(Path.Combine(files.Root, "bf16"), "Expert", MusicModelVariants.Bf16);
        q8.SetCurrent(native); bf16.SetCurrent(transferred);
        Assert.AreEqual(12d, q8.Current().Get("lm_seed")); Assert.AreEqual(MusicModelVariants.Bf16, bf16.Current().Variation);
        var preset = ModelExpertPresets.Create("BF16 рецепт", transferred, "Expert");
        Assert.Throws<InvalidDataException>(() => q8.Save([preset])); bf16.Save([preset]);
        var path = Path.Combine(files.Root, ModelExpertPresets.ExportName(preset.Name, DateTime.Today, transferred.Variation));
        ModelExpertPresets.Export(path, preset); Assert.AreEqual(MusicModelVariants.Bf16, ModelExpertPresets.Import(path).Settings.Variation);
        Assert.Contains("BF16", path);
    }
    [TestMethod]
    public Task SharedProjectRestoresEachVariationAndNeverChangesSubmittedHistory() => ScenarioNavigationTests.Sta(() => {
        using var files = new MusicProjectTests.Files(); var projects = new MusicProjects(Path.Combine(files.Root, "projects"));
        var jobs = new MusicGenerationJobs(Path.Combine(files.Root, "jobs"));
        using var view = new MusicWorkspaceControl(projects, jobs, new MusicOutputPreferences(Path.Combine(files.Root, "output.json")));
        view.ConfigureOutput(files.Root, _ => { }); view.Editor.Lyrics = "Одна песня";
        var q8 = new MusicExpertSettings(); q8.Values["seed"] = 11; view.Generation.SetExpertSettings(q8, false);
        view.Projects.Fix(); var projectId = view.Projects.Current.Id;
        var job = jobs.Create(files.Root, files.Root, "", 1, 30, "", "Одна песня", expert: q8);
        view.Projects.Record(job, view.Projects.Capture());
        view.Projects.SwitchModel(MusicModelVariants.Bf16);
        var bf16 = view.Generation.ExpertSettings; bf16.Values["seed"] = 22; view.Generation.SetExpertSettings(bf16, false);
        view.Projects.SwitchModel(MusicComponentCatalog.ModelId); Assert.AreEqual(11d, view.Generation.ExpertSettings.Get("seed"));
        view.Projects.SwitchModel(MusicModelVariants.Bf16); Assert.AreEqual(22d, view.Generation.ExpertSettings.Get("seed"));
        Assert.AreEqual("Одна песня", view.Editor.Lyrics); Assert.AreEqual(projectId, view.Projects.Current.Id);
        Assert.HasCount(1, view.Projects.Current.Steps);
        Assert.AreEqual(MusicComponentCatalog.ModelId, view.Projects.Current.Steps[0].Snapshot.Variation);
        Assert.AreEqual(11d, view.Projects.Current.Steps[0].Snapshot.Expert.Get("seed"));
        var saved = projects.Load(projectId); Assert.HasCount(2, saved.Saved.ModelSettings);
        view.Projects.New(); view.Projects.Open(projectId); Assert.AreEqual(MusicStudioRuntime.Variation, view.Generation.Variation);
        Assert.AreEqual(22d, view.Generation.ExpertSettings.Get("seed"));
    });
    [TestMethod]
    public void LegacyProjectMigratesOnWriteWithBackupAndIndependentBank()
    {
        using var files = new MusicProjectTests.Files(); var projects = new MusicProjects(files.Root);
        var project = projects.Fix(projects.CreateDraft(), new() { Lyrics = "Старый текст" });
        var path = Path.Combine(files.Root, project.Id + ".project.json");
        File.WriteAllText(path, JsonSerializer.Serialize(project with { Schema = 1 }));
        var old = File.ReadAllText(path); var loaded = projects.Load(project.Id);
        var settings = MusicModelVariants.Defaults(MusicModelVariants.Bf16);
        projects.SaveWorkspace(loaded, loaded.Saved with { Variation = settings.Variation, ModelRevision = MusicModelVariants.Bf16Revision,
            Expert = settings, ModelSettings = new() { [MusicComponentCatalog.ModelId] = loaded.Saved.Expert, [settings.Variation] = settings } });
        Assert.AreEqual(old, File.ReadAllText(path + ".schema1.bak"));
        Assert.AreEqual(2, projects.Load(project.Id).Schema); Assert.AreEqual("Старый текст", projects.Load(project.Id).Saved.Lyrics);
    }
    [TestMethod]
    public void Bf16DownloadManifestPinsAllWeightsAndLibraryFiles()
    {
        var cards = MusicModelVariants.Cards("H:\\Models", MusicModelVariants.Bf16);
        Assert.HasCount(3, cards);
        Assert.AreEqual(7_261_441_640L, cards[0].Files.Single(f => f.RelativePath == "model.safetensors").SizeBytes);
        Assert.AreEqual(530_512_720L, cards[1].Files.Single(f => f.RelativePath == "model.safetensors").SizeBytes);
        Assert.IsTrue(cards.All(c => c.Files.All(f => f.Sha256.Length == 64 && f.SizeBytes > 0 && f.SourceUrl.StartsWith("https://", StringComparison.Ordinal))));
        Assert.IsTrue(cards[2].Files.All(f => f.RelativePath.EndsWith(".whl", StringComparison.Ordinal)));
        Assert.IsFalse(cards[2].Files.Any(f => f.RelativePath.StartsWith("torch-", StringComparison.Ordinal)));
    }
}
