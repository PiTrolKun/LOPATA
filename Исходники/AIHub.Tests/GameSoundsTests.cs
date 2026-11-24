using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.GameSounds;
using NAudio.Wave;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class GameSoundsTests
{
    [TestMethod]
    public Task OpeningAgentSoundDraftFromLibrarySkipsOnlyItsOwnModelPreparation() => ScenarioNavigationTests.Sta(() =>
    {
        var root = Directory.CreateTempSubdirectory("lopata-agent-sound-").FullName;
        var owner = new System.Windows.Window { Left = -30000, Top = -30000, ShowInTaskbar = false, Width = 120, Height = 100 };
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        try
        {
            var store = new GameSoundProjects(root);
            var agent = store.Create();
            agent.ExternalAgentProject = true;
            agent.Draft = "Звон монеты";
            store.Save(agent);
            var manual = store.Create();
            manual.Draft = "Ручной проект";
            manual.Options = manual.Options with { ModelId = "intentionally-unavailable-model" };
            store.Save(manual);
            var legacy = store.Create();
            legacy.Options = manual.Options;
            store.Save(legacy);
            var manualJson = JsonNode.Parse(File.ReadAllBytes(Path.Combine(root, manual.Id + ".json")))!.AsObject();
            Assert.IsFalse(manualJson["externalAgentProject"]!.GetValue<bool>());
            var legacyPath = Path.Combine(root, legacy.Id + ".json");
            var legacyJson = JsonNode.Parse(File.ReadAllBytes(legacyPath))!.AsObject();
            Assert.IsTrue(legacyJson.Remove("externalAgentProject"));
            File.WriteAllText(legacyPath, legacyJson.ToJsonString());
            owner.Show();
            var editor = new Controls.GameSounds.GameSoundsControl(owner, key => key, () => "ru", store);
            var dialogs = new List<string>();
            timer.Tick += (_, _) =>
            {
                foreach (System.Windows.Window window in owner.OwnedWindows)
                {
                    if (!window.IsVisible) continue;
                    var id = System.Windows.Automation.AutomationProperties.GetAutomationId(window);
                    var elements = ScenarioNavigationTests.LogicalDescendants(window).ToArray();
                    var buttonId = id == "GameSounds.ChooseModel.Window" ? "GameSounds.Projects" : "GameSounds.OpenProject";
                    if (id == "GameSounds.Projects.Window")
                    {
                        var list = elements.OfType<System.Windows.Controls.ListBox>().Single();
                        list.SelectedItem = list.Items.OfType<System.Windows.Controls.ListBoxItem>().Single(item => Equals(item.Tag, agent.Id));
                    }
                    var button = elements.OfType<System.Windows.Controls.Button>().SingleOrDefault(b =>
                        System.Windows.Automation.AutomationProperties.GetAutomationId(b) == buttonId);
                    dialogs.Add(id);
                    if (button is null) window.Close();
                    else button.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    break;
                }
            };
            timer.Start();
            Assert.IsTrue(editor.EnterAsync().GetAwaiter().GetResult());
            timer.Stop();
            CollectionAssert.AreEqual(new[] { "GameSounds.ChooseModel.Window", "GameSounds.Projects.Window" }, dialogs);
            var prompt = ScenarioNavigationTests.LogicalDescendants(editor).OfType<System.Windows.Controls.TextBox>()
                .Single(box => System.Windows.Automation.AutomationProperties.GetAutomationId(box) == "GameSounds.Prompt");
            Assert.AreEqual(agent.Draft, prompt.Text);
            foreach (var localProject in new[] { manual, legacy })
            {
                var error = Assert.ThrowsExactly<InvalidDataException>(() => editor.OpenProjectAsync(localProject.Id).GetAwaiter().GetResult());
                Assert.AreEqual("GameSounds.NoModel", error.Message);
                Assert.AreEqual(agent.Draft, prompt.Text);
            }
            Assert.IsTrue(editor.Leave());
        }
        finally { timer.Stop(); owner.Close(); Directory.Delete(root, recursive: true); }
    });

    [TestMethod]
    public void SixPresetsAreReproducibleAndPartialEditPreservesOtherSettings()
    {
        var first = new HashSet<string>();
        foreach (var (key, parameters) in GameSoundRecipes.Presets)
        {
            var recipe = GameSoundRecipe.Create(key, parameters);
            var audio = GameSoundSynthesizer.Render([new(recipe)]);
            CollectionAssert.AreEqual(audio, GameSoundSynthesizer.Render([new(recipe)]));
            Assert.AreEqual(parameters.DurationMs * 44100 / 1000, audio.Length);
            Assert.AreEqual(0, audio[0]);
            Assert.AreEqual(0, audio[^1]);
            Assert.IsTrue(audio.Max(sample => Math.Abs((int)sample)) > 100);
            Assert.IsTrue(first.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(GameSoundSynthesizer.ToBytes(audio)))));
        }

        var coin = GameSoundRecipe.Create("Coin", GameSoundRecipes.Presets[0].Sound);
        var edit = GameSoundRecipes.Parse("{\"releaseMs\":18}", coin);
        Assert.AreEqual(coin.Sound with { ReleaseMs = 18 }, edit.Sound);
        Assert.AreEqual(coin.Name, edit.Name);
        Assert.ThrowsExactly<InvalidDataException>(() => GameSoundRecipes.Parse("{\"releaseMs\":900}", coin));
        Assert.ThrowsExactly<InvalidDataException>(() => GameSoundRecipes.Parse("{\"path\":\"output.wav\"}", coin));
    }

    [TestMethod]
    public async Task SavedMixAndWavRoundTripWithSamePcmAndActualProvenance()
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-game-sounds-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var model = new OutputModelUse("sample-model", "Sample model", "rev-42", "game-sound/recipe");
            var coin = GameSoundRecipe.Create("Coin", GameSoundRecipes.Presets[0].Sound, [model]);
            var laser = GameSoundRecipe.Create("Laser", GameSoundRecipes.Presets[2].Sound);
            var hit = GameSoundRecipe.Create("Hit", GameSoundRecipes.Presets[3].Sound);
            GameSoundSource[] layers = [new(coin), new(laser, 110, 45), new(hit, 200, 30, false)];
            var document = GameSoundFiles.Create("Test effect", layers, isMix: true);
            var recipe = Path.Combine(root, "test.lopataSfx.json");
            var audio = Path.Combine(root, "test.wav");
            GameSoundFiles.SaveRecipe(recipe, document, CancellationToken.None);
            var reopened = GameSoundFiles.Load(recipe);
            Assert.IsTrue(reopened.IsMix);
            Assert.AreEqual(3, reopened.Layers.Length);
            Assert.AreEqual(110, reopened.Layers[1].StartMs);
            Assert.AreEqual(45, reopened.Layers[1].Level);
            Assert.IsFalse(reopened.Layers[2].Enabled);
            var expected = GameSoundSynthesizer.ToBytes(GameSoundSynthesizer.Render(layers));
            CollectionAssert.AreEqual(expected, GameSoundSynthesizer.ToBytes(GameSoundSynthesizer.Render(reopened.Layers)));
            Assert.IsFalse(expected.SequenceEqual(GameSoundSynthesizer.ToBytes(GameSoundSynthesizer.Render([layers[0], layers[1] with { StartMs = 0 }]))));
            await GameSoundFiles.SaveAudioAsync(audio, reopened, MusicAudioFormat.Wav, CancellationToken.None);
            using (var wav = new WaveFileReader(audio))
            {
                Assert.AreEqual(44100, wav.WaveFormat.SampleRate);
                Assert.AreEqual(1, wav.WaveFormat.Channels);
                Assert.AreEqual(16, wav.WaveFormat.BitsPerSample);
                var pcm = new byte[wav.Length];
                wav.ReadExactly(pcm);
                CollectionAssert.AreEqual(expected, pcm);
            }
            var tagged = await File.ReadAllBytesAsync(audio);
            var metadata = Encoding.UTF8.GetString(tagged);
            StringAssert.Contains(metadata, "game-sounds/wav");
            StringAssert.Contains(metadata, "rev-42");
            StringAssert.Contains(metadata, GameSoundSynthesizer.Revision);
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
                GameSoundFiles.SaveAudioAsync(audio, reopened, MusicAudioFormat.Wav, new CancellationToken(canceled: true)));
            CollectionAssert.AreEqual(tagged, await File.ReadAllBytesAsync(audio));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [DataRow(MusicAudioFormat.Opus)]
    [DataRow(MusicAudioFormat.Mp3)]
    public async Task CompressedExportsAreSmallerAndPlayableWithEmbeddedTags(MusicAudioFormat format)
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-game-audio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var duration in new[] { 25, GameSoundRecipes.MaxMilliseconds })
            {
                var tone = GameSoundRecipe.Create("Лазер", new GameSoundParameters { DurationMs = duration, ReleaseMs = 10 });
                var document = GameSoundFiles.Create("Лазер #1", [new(tone)], isMix: true);
                var path = Path.Combine(root, "Лазер" + MusicOutputSettings.Extension(format));
                await GameSoundFiles.SaveAudioAsync(path, document, format, CancellationToken.None);
                var decoded = await GameSoundAudio.ReadPcmAsync(path, CancellationToken.None);
                var bytes = duration * GameSoundSynthesizer.SampleRate / 1000 * sizeof(short);
                Assert.IsLessThanOrEqualTo(GameSoundSynthesizer.Format.AverageBytesPerSecond / 100,
                    Math.Abs(decoded.Length - bytes));
                Assert.IsTrue(decoded.Any(value => value != 0));
                if (duration > 25) Assert.IsLessThan(bytes, new FileInfo(path).Length);
                using var probe = JsonDocument.Parse(await MusicAudioRuntime.Default.RunAsync(true,
                    ["-v", "error", "-show_streams", "-show_format", "-of", "json", path], CancellationToken.None));
                var tags = AudioConversionMetadata.ProbeTags(probe.RootElement);
                Assert.AreEqual("Лазер #1", tags["title"]);
                StringAssert.Contains(tags["LOPATA_PROVENANCE"], GameSoundFiles.AudioScenario(format));
                StringAssert.Contains(tags["LOPATA_GAME_SOUND"], "\"Kind\":\"mix\"");
                Assert.AreEqual(GameSoundSynthesizer.Revision, tags["LOPATA_SYNTHESIZER"]);
            }
            Assert.AreEqual(1, Directory.GetFiles(root).Length);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void ProjectRetainsCompletedLayersAndInterruptedRequestAcrossReopen()
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-game-project-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new GameSoundProjects(root);
            var project = store.Create();
            Assert.AreEqual(MusicAudioFormat.Opus, project.OutputFormat);
            project.OutputFormat = MusicAudioFormat.Mp3;
            project.ExportRecipeWithAudio = true;
            var recipe = GameSoundRecipe.Create("Explosion", GameSoundRecipes.Presets[4].Sound);
            var pendingId = Guid.NewGuid().ToString("N");
            project.Name = "Alarm";
            project.Replace(recipe); project.SelectedId = recipe.Id; project.Orchestrate = true;
            project.Tracks.Add(new(recipe.Id, 120, 85));
            project.Pending = new(pendingId, "Add a layer", true, true, null, "en", project.Sources(true).ToArray());
            project.Messages.Add(new("user", "Add a layer"));
            project.Draft = "Unsent prompt";
            store.Save(project);
            var loaded = store.Load(project.Id);
            Assert.AreEqual(project.Name, loaded.Name);
            Assert.AreEqual("Unsent prompt", loaded.Draft);
            Assert.AreEqual(pendingId, loaded.Pending?.Id);
            Assert.AreEqual(recipe.Id, loaded.Selected?.Id);
            Assert.AreEqual(120, loaded.Pending?.Layers?.Single().StartMs);
            Assert.AreEqual(85, loaded.Tracks.Single().Level);
            Assert.AreEqual(1, loaded.Messages.Count);
            Assert.AreEqual(MusicAudioFormat.Mp3, loaded.OutputFormat);
            Assert.IsTrue(loaded.ExportRecipeWithAudio);
            var path = Path.Combine(root, project.Id + ".json");
            var legacy = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Assert.IsTrue(legacy.Remove("outputFormat"));
            File.WriteAllText(path, legacy.ToJsonString());
            var reopened = store.Load(project.Id);
            Assert.AreEqual(MusicAudioFormat.Wav, reopened.OutputFormat);
            Assert.IsTrue(reopened.ExportRecipeWithAudio);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task LargeQwenUsesTheExistingLicenseIdentityForBothEntryPoints()
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-game-license-" + Guid.NewGuid().ToString("N"));
        try
        {
            var license = new ComponentLicenseService(Path.Combine(AppContext.BaseDirectory, "Licenses"),
                Path.Combine(root, "receipts.json"));
            var calls = 0;
            await license.EnsureAsync([ManagedModelCatalog.Qwen36ExecutorArtifactId, ManagedModelCatalog.Qwen36ExecutorRepository],
                entries =>
                {
                    calls++;
                    Assert.AreEqual(ManagedModelCatalog.Qwen36ExecutorRepository, entries.Single().Id);
                    return Task.FromResult(true);
                }, CancellationToken.None);
            await license.EnsureAsync([ManagedModelCatalog.Qwen36ExecutorArtifactId],
                _ => { calls++; return Task.FromResult(false); }, CancellationToken.None);
            Assert.AreEqual(1, calls);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task InstalledModelsKeepWarmAndAcceptStructuredGeneration()
    {
        if (Environment.GetEnvironmentVariable("LOPATA_TEST_GAME_SOUNDS_NATIVE") != "1") Assert.Inconclusive("Requires installed models.");
        var cards = GameSoundModels.Available(new StorageSettingsStore().LoadOrCreate(), new());
        CollectionAssert.AreEqual(new[] { ManagedModelCatalog.CoreArtifactId, "model-fcb9b1508fc0d3f38bc2f3e5",
            ManagedModelCatalog.LetItOutArtifactId }, cards.Select(c => c.ModelArtifactId).ToArray());
        var clock = Stopwatch.StartNew();
        using var session = new GameSoundModelSession();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        try
        {
            var requested = Environment.GetEnvironmentVariable("LOPATA_TEST_GAME_SOUNDS_MODEL");
            var card = int.TryParse(requested, out var index) ? cards[index] : cards[0];
            var mode = Enum.TryParse<GameSoundPlacement>(Environment.GetEnvironmentVariable("LOPATA_TEST_GAME_SOUNDS_PLACEMENT"), out var placement)
                ? placement : GameSoundPlacement.Auto;
            var options = new GameSoundModelOptions(card.ModelArtifactId, mode);
            await session.WarmAsync(card, options.Placement, timeout.Token);
            var initial = session.Session!;
            Console.WriteLine($"{card.DisplayName}: {initial.Mode} · {initial.Context} tokens · PID {initial.Pid}");
            var coin = await session.AskAsync(card, options,
                new(Guid.NewGuid().ToString("N"), "Короткий звонкий звук подбора монетки, с повышением тона.", true, false, null, "ru"),
                [], [], _ => { }, Console.WriteLine, timeout.Token);
            Assert.IsNotNull(coin.Recipe);
            GameSoundRecipes.Validate(coin.Recipe.Sound);
            var laser = await session.AskAsync(card, options,
                new(Guid.NewGuid().ToString("N"), "Create one descending retro laser layer with a short tail.", true, true, null, "en"),
                [], [new(coin.Recipe)], _ => { }, Console.WriteLine, timeout.Token);
            Assert.IsNotNull(laser.Recipe);
            Assert.AreNotEqual(coin.Recipe.Id, laser.Recipe.Id);
            var edited = await session.AskAsync(card, options,
                new(Guid.NewGuid().ToString("N"), "Set releaseMs to exactly 10. Keep all other parameters.", true, true, laser.Recipe, "en"),
                [], [], _ => { }, Console.WriteLine, timeout.Token);
            Assert.IsNotNull(edited.Recipe);
            Assert.AreEqual(laser.Recipe.Id, edited.Recipe.Id);
            Assert.AreEqual(laser.Recipe.Sound with { ReleaseMs = 10 }, edited.Recipe.Sound);
            Assert.AreEqual(initial.Pid, session.Session!.Pid);
            Console.WriteLine($"{coin.Text} · {laser.Text} · {clock.Elapsed}");
        }
        finally { await session.RetireAsync(); }
    }
}
