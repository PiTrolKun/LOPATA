using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services.LivingArcade;

namespace AIHub.Tests;

[TestClass]
public sealed class LivingArcadeSceneryArtTests
{
    [TestMethod]
    public void StableChapterChoiceKeepsABuiltInAlternativeAtAboutOneInEight()
    {
        var builtIn = 0;
        var total = 0;
        foreach (var seed in new[] { 0, 1, -1, int.MinValue, int.MaxValue, 734169 })
        for (var chapter = 0; chapter < 256; chapter++)
        {
            var generated = LivingArcadeSceneryArtPolicy.ShouldGenerate(seed, chapter);
            Assert.AreEqual(generated, LivingArcadeSceneryArtPolicy.ShouldGenerate(seed, chapter));
            if (!generated) builtIn++;
            total++;
        }
        Assert.IsTrue(builtIn > total * .09 && builtIn < total * .17, $"Built-in share {builtIn}/{total} must remain near12.5%.");
        Assert.AreEqual(0, LivingArcadeSceneryArtPolicy.ChapterFor(int.MinValue));
        Assert.AreEqual(0, LivingArcadeSceneryArtPolicy.ChapterFor(3));
        Assert.AreEqual(1, LivingArcadeSceneryArtPolicy.ChapterFor(4));
        Assert.IsTrue(LivingArcadeSceneryArtPolicy.ShouldGenerate(int.MinValue, int.MaxValue)
            == LivingArcadeSceneryArtPolicy.ShouldGenerate(int.MinValue, int.MaxValue));
    }

    [TestMethod]
    public void OriginalSceneryHasFiniteGeometryAndCannotEmbedExecutableOrExternalContent()
    {
        var art = Art();
        LivingArcadeSceneryArtPolicy.Validate(art);
        var copy = LivingArcadeSceneryArtPolicy.Copy(art)!;
        copy.RoadsideObjects[0].Name = "Changed copy";
        Assert.AreEqual("Folded crystal observatory", art.RoadsideObjects[0].Name);
        Assert.ThrowsExactly<InvalidDataException>(() => LivingArcadeSceneryArtPolicy.Validate(
            Modify(value => value.GroundShapes.Add(new() { Kind = "image", Data = "https://example.invalid/picture" }))));
        Assert.ThrowsExactly<InvalidDataException>(() => LivingArcadeSceneryArtPolicy.Validate(
            Modify(value => value.HorizonShapes[0].Width = double.NaN)));
        Assert.ThrowsExactly<InvalidDataException>(() => LivingArcadeSceneryArtPolicy.Validate(
            Modify(value => value.RoadsideObjects[0].Shapes.Add(new() { Kind = "path", Data = "<svg><script/></svg>" }))));
        Assert.ThrowsExactly<InvalidDataException>(() => LivingArcadeSceneryArtPolicy.Validate(
            Modify(value => value.RoadsideObjects[0].Shapes.Add(new() { Kind = "rect", X = 250, Width = 20, Height = 3 }))));
        Assert.ThrowsExactly<InvalidDataException>(() => LivingArcadeSceneryArtPolicy.Validate(
            Modify(value => value.RoadsideObjects = Enumerable.Range(0, 7).Select(_ => value.RoadsideObjects[0]).ToList())));
        Assert.ThrowsExactly<InvalidDataException>(() => LivingArcadeSceneryArtPolicy.Validate(
            Modify(value => value.GroundShapes = Enumerable.Range(0, 129).Select(_ => value.HorizonShapes[0]).ToList())));
    }

    [TestMethod]
    public void ArtistOutputGetsRealPinnedProvenanceAndRejectsMissingOrDuplicateFields()
    {
        var art = Art();
        art.Credits = [new("fake", "model-chosen", "fiction")];
        var json = JsonSerializer.Serialize(art, LivingArcadeLevelPolicy.JsonOptions);
        var parsed = LivingArcadeGeneration.ParseGeneratedScenery(json);
        Assert.AreEqual(1, parsed.Credits.Count);
        Assert.AreEqual("Qwen/Qwen2.5-Coder-3B-Instruct-GGUF", parsed.Credits[0].ModelId);
        Assert.AreEqual(LivingArcadeModels.ArtistRevision, parsed.Credits[0].Revision);
        Assert.ThrowsExactly<InvalidDataException>(() => LivingArcadeGeneration.ParseGeneratedScenery("{\"titleEn\":\"Incomplete\"}"));
        var duplicate = json.Insert(1, "\"titleEn\":\"Duplicate\",");
        Assert.ThrowsExactly<InvalidDataException>(() => LivingArcadeGeneration.ParseGeneratedScenery(duplicate));
    }

    [TestMethod]
    public Task GenuineOutOfBoundsArtistResponseIsFittedAndRendersInBothViews() => ScenarioNavigationTests.Sta(() =>
    {
        // The original actual-model content is retained; HTTP envelopes and local machine paths are excluded.
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "LivingArcade", "scenery-native-out-of-bounds.json"));
        var art = LivingArcadeGeneration.ParseGeneratedScenery(json);
        Assert.IsTrue(art.GeometryFitted);
        Assert.AreEqual("The first road", art.TitleEn);
        Assert.AreEqual(5, art.RoadsideObjects.Count);
        Assert.IsTrue(art.HorizonShapes.Where(shape => shape.Kind == "path").All(shape =>
            shape.X == 0 && shape.Y == 0 && shape.Width == 0 && shape.Height == 0));
        LivingArcadeSceneryArtPolicy.Validate(art);
        var renderer = new AIHub.Controls.LivingArcade.LivingArcadeGeneratedBackground();
        renderer.Prepare(art);
        try
        {
            foreach (var perspective in new[] { false, true })
            {
                var visual = new System.Windows.Media.DrawingVisual();
                using (var drawing = visual.RenderOpen()) renderer.Draw(drawing, 800, 600, 100, 600, 17, perspective);
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(800, 600, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var pixels = new byte[800 * 600 * 4]; bitmap.CopyPixels(pixels, 800 * 4, 0);
                Assert.IsTrue(pixels.Where((_, index) => index % 4 == 3).Any(alpha => alpha > 0));
            }
        }
        finally { renderer.Clear(); }
    });

    [TestMethod]
    public void SafeCanvasFitKeepsRelativePlacementAndDoesNotChangeEnemyVisualRules()
    {
        var art = Art();
        art.RoadsideObjects[0].Shapes =
        [new() { Kind = "rect", X = 300, Y = -100, Width = 200, Height = 400 },
         new() { Kind = "ellipse", X = 350, Y = 0, Width = 50, Height = 100 }];
        LivingArcadeSceneryGeometryNormalizer.Fit(art);
        LivingArcadeSceneryArtPolicy.Validate(art);
        Assert.IsTrue(art.GeometryFitted);
        var first = art.RoadsideObjects[0].Shapes[0]; var second = art.RoadsideObjects[0].Shapes[1];
        Assert.AreEqual(first.Width / 4, second.Width, .00001);
        Assert.AreEqual(first.Height / 4, second.Height, .00001);
        Assert.AreEqual(first.Width / 4, second.X - first.X, .00001);
        Assert.AreEqual(first.Height / 4, second.Y - first.Y, .00001);
        Assert.ThrowsExactly<InvalidDataException>(() => LivingArcadeVisualPolicy.Validate(new()
        { Mode = "vector", Frames = [new() { Shapes = [new() { Kind = "rect", X = 300, Width = 20, Height = 20 }] }] }));
        var untouched = Art(); untouched.GeometryFitted = true;
        LivingArcadeSceneryGeometryNormalizer.Fit(untouched);
        Assert.IsFalse(untouched.GeometryFitted, "Only actual host corrections can claim normalization provenance.");
    }

    [TestMethod]
    public void SceneryOnlyFitSimplifiesLongPathsButStillRejectsCodeAndUnboundedNumbers()
    {
        var art = Art();
        art.RoadsideObjects[0].Shapes = [new() { Kind = "path", Data = "M300,0 " +
            string.Join(" ", Enumerable.Range(1, 450).Select(index => $"L{300 + index},{index % 2 * 100}")) }];
        LivingArcadeSceneryGeometryNormalizer.Fit(art);
        LivingArcadeSceneryArtPolicy.Validate(art);
        var path = art.RoadsideObjects[0].Shapes[0].Data!;
        Assert.IsTrue(path.Count(character => character is 'M' or 'L') <= 192);
        Assert.IsTrue(path.Length <= 4096);
        foreach (var unsafeData in new[] { "<svg><script/></svg>", "https://example.invalid/art", "M1e309,0L0,1", "M0,0L4097,0" })
        {
            var invalid = Art(); invalid.HorizonShapes[0].Data = unsafeData;
            Assert.ThrowsExactly<InvalidDataException>(() => LivingArcadeSceneryGeometryNormalizer.Fit(invalid));
        }
        var nonfinite = Art(); nonfinite.GroundShapes[0].Width = double.NaN;
        Assert.ThrowsExactly<InvalidDataException>(() => LivingArcadeSceneryGeometryNormalizer.Fit(nonfinite));
    }

    [TestMethod]
    public void ChapterArtUpdatesPreparedStagesAndArchivesWithoutChangingTheirCombatOrRunProgress()
    {
        WithStore(store =>
        {
            var run = store.Create("Old prepared stages", 74);
            store.TrySaveLevel(run.Id, LivingArcadeLevelPolicy.CreateFirst(74) with { Number = 2, Authored = false });
            store.TrySaveLevel(run.Id, LivingArcadeLevelPolicy.CreateFirst(74) with { Number = 3, Authored = false });
            run = store.Load(run.Id)!;
            var gameplay = run.Levels.ToDictionary(pair => pair.Key, pair => WithoutArt(pair.Value));
            var progress = Progress(run);
            var art = Art();
            Assert.IsTrue(store.TrySaveChapterScenery(run.Id, 2, new(run.Seed, 0, "generated", art)));
            run = store.Load(run.Id)!;
            Assert.AreEqual(progress, Progress(run));
            foreach (var (number, level) in run.Levels)
            {
                Assert.AreEqual(gameplay[number], WithoutArt(level));
                Assert.AreEqual(JsonSerializer.Serialize(art), JsonSerializer.Serialize(level.Scenery));
                var archive = Path.Combine(store.RootDirectory, run.Id, "levels", number + ".json");
                var archived = LivingArcadeLevelPolicy.Parse(File.ReadAllText(archive));
                Assert.AreEqual(JsonSerializer.Serialize(art), JsonSerializer.Serialize(archived.Scenery));
                Assert.AreEqual(gameplay[number], WithoutArt(archived));
            }
            store.Update(run.Id, value => value.CurrentLevel = 3);
            Assert.AreEqual("generated", new LivingArcadeRunStore(store.RootDirectory).GetChapterScenery(run.Id, 3)!.Outcome);
            Assert.IsFalse(store.TrySaveChapterScenery(run.Id, 3, new(run.Seed, 0, "failed", Failure: "LateFailure")),
                "Reopening and retries cannot replace an accepted chapter with a fallback or reroll.");
            store.TrySaveLevel(run.Id, LivingArcadeLevelPolicy.CreateFirst(74) with { Number = 4, Authored = false });
            Assert.IsNull(store.Load(run.Id)!.Levels[4].Scenery, "The following chapter never inherits the old place by accident.");
        });
    }

    [TestMethod]
    public void TerminalBuiltinAndFailureSurviveReopenAndAlsoApplyToLaterPreparedStages()
    {
        foreach (var outcome in new[] { "builtin", "failed" })
        WithStore(store =>
        {
            var run = store.Create("Terminal " + outcome, 91);
            var progress = Progress(run);
            Assert.IsTrue(store.TrySaveChapterScenery(run.Id, 1, new(run.Seed, 0, outcome, Failure: outcome == "failed" ? "InvalidDataException" : null)));
            var reopened = new LivingArcadeRunStore(store.RootDirectory);
            Assert.AreEqual(outcome, reopened.GetChapterScenery(run.Id, 2)!.Outcome);
            Assert.IsFalse(reopened.TrySaveChapterScenery(run.Id, 1, new(run.Seed, 0, "generated", Art())));
            reopened.TrySaveLevel(run.Id, LivingArcadeLevelPolicy.CreateFirst(91) with { Number = 2, Scenery = Art() });
            var after = reopened.Load(run.Id)!;
            Assert.AreEqual(progress, Progress(after));
            Assert.IsNull(after.Levels[1].Scenery);
            Assert.IsNull(after.Levels[2].Scenery);
        });
    }

    [TestMethod]
    public void NewPreparedLevelReceivesTheAlreadyConfirmedChapterArt()
    {
        WithStore(store =>
        {
            var run = store.Create("Reuse one location", 87);
            store.TrySaveChapterScenery(run.Id, 1, new(run.Seed, 0, "generated", Art()));
            Assert.IsTrue(store.TrySaveLevel(run.Id, LivingArcadeLevelPolicy.CreateFirst(87) with { Number = 2 }));
            var reopened = store.Load(run.Id)!;
            Assert.AreEqual(JsonSerializer.Serialize(reopened.Levels[1].Scenery), JsonSerializer.Serialize(reopened.Levels[2].Scenery));
        });
    }

    [TestMethod]
    public void CorruptedOptionalReceiptUsesBackupOrTerminalFallbackWithoutRerollingTheChapter()
    {
        WithStore(store =>
        {
            var run = store.Create("Recover optional art", 87);
            var path = Path.Combine(store.RootDirectory, run.Id, "scenery", "0.json");
            store.TrySaveChapterScenery(run.Id, 1, new(run.Seed, 0, "builtin"));
            File.Copy(path, path + ".bak");
            File.WriteAllText(path, "{broken optional json");
            Assert.AreEqual("builtin", store.GetChapterScenery(run.Id, 1)!.Outcome);
            File.WriteAllText(path + ".bak", "also broken");
            var fallback = store.GetChapterScenery(run.Id, 1)!;
            Assert.AreEqual("failed", fallback.Outcome);
            Assert.AreEqual("UnreadableOptionalReceipt", fallback.Failure);
            store.TrySaveChapterScenery(run.Id, 1, fallback);
            Assert.AreEqual("failed", new LivingArcadeRunStore(store.RootDirectory).GetChapterScenery(run.Id, 1)!.Outcome);
            Assert.AreEqual(1, store.Load(run.Id)!.CurrentLevel);
        });
    }

    internal static LivingArcadeGeneratedScenery Art() => new()
    {
        TitleRu = "Сад складчатого кристалла", TitleEn = "Folded crystal garden",
        HorizonShapes = [new() { Kind = "path", Data = "M0,120 L28,70 L55,110 L85,55 L120,90 L180,65 L256,120 Z", Fill = "#304C59" }],
        GroundShapes = [new() { Kind = "line", X = 0, Y = 128, Width = 256, Height = 0, Fill = "none", Stroke = "#30444D" }],
        RoadsideObjects = [new() { Name = "Folded crystal observatory", Shapes =
        [new() { Kind = "path", Data = "M28,240 L80,50 L128,20 L208,180 L228,240 Z", Fill = "#465B67", Stroke = "#83929E" },
         new() { Kind = "ellipse", X = 77, Y = 70, Width = 100, Height = 65, Fill = "#72A99A" },
         new() { Kind = "line", X = 128, Y = 140, Width = 0, Height = 100, Fill = "none", Stroke = "#83929E", StrokeWidth = 6 }] }]
    };

    private static LivingArcadeGeneratedScenery Modify(Action<LivingArcadeGeneratedScenery> change)
    { var result = Art(); change(result); return result; }
    internal static string WithoutArt(LivingArcadeLevel level) => JsonSerializer.Serialize(level with { Scenery = null }, LivingArcadeLevelPolicy.JsonOptions);
    internal static string Progress(LivingArcadeRun run) => JsonSerializer.Serialize(new
    {
        run.CurrentLevel, run.Attempts, run.DeathProgress, run.Penalty, run.EntryState, run.PendingHero,
        run.PendingBossRewardLevel, run.BossRewards, run.FirstEventsPrepared, run.Attempt, run.History, run.Draft
    });
    private static void WithStore(Action<LivingArcadeRunStore> test)
    {
        var directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "arcade-scenery-art-" + Guid.NewGuid().ToString("N")));
        try { test(new(directory)); }
        finally
        {
            if (Directory.Exists(directory) && directory.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(directory).StartsWith("arcade-scenery-art-", StringComparison.Ordinal)) Directory.Delete(directory, true);
        }
    }
}
