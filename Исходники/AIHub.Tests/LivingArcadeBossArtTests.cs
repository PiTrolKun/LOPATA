using System.IO;
using System.Reflection;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services.LivingArcade;

namespace AIHub.Tests;

[TestClass]
public sealed class LivingArcadeBossArtTests
{
    [TestMethod]
    public void BossArtistAcceptsDetailedBodiesAndPinsActualProvenance()
    {
        var level = Level(); var library = LivingArcadeLibrary.Load(); var art = Art();
        art.Source = "authored-fallback"; art.Credits = [new("fiction", "made-up", "wrong")];
        var parsed = LivingArcadeGeneration.ParseGeneratedBossArt(JsonSerializer.Serialize(art, LivingArcadeLevelPolicy.JsonOptions), level, library);
        Assert.AreEqual("model", parsed.Source);
        Assert.AreEqual("Qwen/Qwen2.5-Coder-7B-Instruct-GGUF", parsed.Credits.Single().ModelId);
        Assert.AreEqual(LivingArcadeModels.MainRevision, parsed.Credits.Single().Revision);
        Assert.AreEqual(32, parsed.Visual.Frames.Single().Shapes.Count);
        Assert.AreEqual(0, parsed.AssetCredits.Count);
        Assert.ThrowsExactly<InvalidDataException>(() => LivingArcadeGeneration.ParseGeneratedBossArt("{\"titleEn\":\"Missing body\"}", level, library));
        var duplicate = JsonSerializer.Serialize(art, LivingArcadeLevelPolicy.JsonOptions).Insert(1, "\"titleEn\":\"Duplicate\",");
        Assert.ThrowsExactly<InvalidDataException>(() => LivingArcadeGeneration.ParseGeneratedBossArt(duplicate, level, library));
        var invalid = Art(); invalid.Visual.Frames[0].Shapes[0].X = 999;
        Assert.ThrowsExactly<InvalidDataException>(() => LivingArcadeGeneration.ParseGeneratedBossArt(JsonSerializer.Serialize(invalid, LivingArcadeLevelPolicy.JsonOptions), level, library));
    }

    [TestMethod]
    public void BossArtistRejectsInvisibleRepeatedOrProceduralDetailAndUsesRealLibraryCredits()
    {
        var level = Level(); var library = LivingArcadeLibrary.Load();
        foreach (var form in new[] { "transparent", "repeat", "paint-duplicates", "unpainted-lines", "tiny", "procedural" })
        {
            var bad = Art();
            if (form == "transparent") foreach (var shape in bad.Visual.Frames[0].Shapes) shape.Opacity = 0;
            if (form == "repeat") bad.Visual.Frames[0].Shapes = Enumerable.Repeat(bad.Visual.Frames[0].Shapes[0], 32).ToList();
            if (form == "paint-duplicates") for (var index = 0; index < 32; index++)
            { var shape = bad.Visual.Frames[0].Shapes[index]; shape.Kind = "rect"; shape.X = 20; shape.Y = 20; shape.Fill = "#" + index.ToString("X6"); }
            if (form == "unpainted-lines") foreach (var shape in bad.Visual.Frames[0].Shapes)
            { shape.Kind = "line"; shape.Stroke = "none"; }
            if (form == "tiny") foreach (var shape in bad.Visual.Frames[0].Shapes) shape.Width = shape.Height = .1;
            if (form == "procedural") { bad.Visual.Mode = "procedural"; bad.Visual.Frames = []; }
            Assert.ThrowsExactly<InvalidDataException>(() => LivingArcadeGeneration.ParseGeneratedBossArt(JsonSerializer.Serialize(bad, LivingArcadeLevelPolicy.JsonOptions), level, library), form);
        }
        var basis = LivingArcadeBossArtCatalog.Shortlist(library, level.TitleEn + " " + level.DescriptionEn, level.Seed).First();
        var art = Art(); art.Visual = new() { Mode = "library", AssetId = basis.Id, Animation = "bob",
            Decorations = Art().Visual.Frames[0].Shapes.Take(4).ToList() };
        var accepted = LivingArcadeGeneration.ParseGeneratedBossArt(JsonSerializer.Serialize(art, LivingArcadeLevelPolicy.JsonOptions), level, library);
        var proof = accepted.AssetCredits.Single(); var pack = library.Packs.Single(item => item.Id == basis.PackId);
        Assert.AreEqual(basis.Id, proof.AssetId); Assert.AreEqual(library.AuditRevision, proof.LibraryRevision);
        Assert.AreEqual(pack.Author, proof.Author); Assert.AreEqual(pack.License, proof.License); Assert.AreEqual(pack.SourceUrl, proof.SourceUrl);
        art.Visual.Decorations.Clear();
        Assert.ThrowsExactly<InvalidDataException>(() => LivingArcadeGeneration.ParseGeneratedBossArt(JsonSerializer.Serialize(art, LivingArcadeLevelPolicy.JsonOptions), level, library));
    }

    [TestMethod]
    public void BossSchemaAllowsRichBodiesWithoutGrantingCombatFields()
    {
        var text = JsonSerializer.Serialize(LivingArcadeGeneration.BossArtistResponseFormat(Level(), LivingArcadeLibrary.Load()));
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement.GetProperty("json_schema").GetProperty("schema");
        CollectionAssert.AreEquivalent(new[] { "titleRu", "titleEn", "visual" }, root.GetProperty("properties").EnumerateObject().Select(property => property.Name).ToArray());
        var modes = root.GetProperty("properties").GetProperty("visual").GetProperty("anyOf");
        var vector = modes.EnumerateArray().Single(mode => mode.GetProperty("properties").GetProperty("mode").GetProperty("enum")[0].GetString() == "vector");
        Assert.AreEqual(128, vector.GetProperty("properties").GetProperty("frames").GetProperty("items").GetProperty("properties").GetProperty("shapes").GetProperty("maxItems").GetInt32());
        Assert.AreEqual(3, vector.GetProperty("properties").GetProperty("frames").GetProperty("maxItems").GetInt32());
        Assert.IsFalse(root.GetProperty("additionalProperties").GetBoolean());
    }

    [TestMethod]
    public void StoredBossArtOnlyChangesOptionalArtAndCannotRerollOnReopen()
    {
        WithStore(store =>
        {
            var run = store.Create("Old boss", 90);
            Assert.IsTrue(store.TrySaveLevel(run.Id, Level()));
            store.Update(run.Id, item => { item.CurrentLevel = 3; item.Attempts = 8; item.DeathProgress = 110; item.Penalty = "heavy"; });
            run = store.Load(run.Id)!;
            var original = WithoutArt(run.Levels[3]); var progress = LivingArcadeSceneryArtTests.Progress(run);
            var art = Art();
            Assert.IsTrue(store.TrySaveBossArt(run.Id, 3, new(run.Seed, 3, "generated", art)));
            var reopened = new LivingArcadeRunStore(store.RootDirectory); var after = reopened.Load(run.Id)!;
            Assert.AreEqual(original, WithoutArt(after.Levels[3]));
            Assert.AreEqual(progress, LivingArcadeSceneryArtTests.Progress(after));
            Assert.AreEqual("Iron archivist", after.Levels[3].BossArt!.TitleEn);
            var archived = LivingArcadeLevelPolicy.Parse(File.ReadAllText(Path.Combine(store.RootDirectory, run.Id, "levels", "3.json")));
            Assert.AreEqual(original, WithoutArt(archived)); Assert.IsNotNull(archived.BossArt);
            Assert.IsFalse(reopened.TrySaveBossArt(run.Id, 3, new(run.Seed, 3, "failed", Failure: "Late failure")));
            var next = Art(); next.TitleEn = "A reroll";
            Assert.IsFalse(reopened.TrySaveBossArt(run.Id, 3, new(run.Seed, 3, "generated", next)));
            Assert.AreEqual("Iron archivist", reopened.Load(run.Id)!.Levels[3].BossArt!.TitleEn);
            var firstArchived = LivingArcadeLevelPolicy.Parse(File.ReadAllText(Path.Combine(store.RootDirectory, run.Id, "levels", "1.json")));
            Assert.IsNull(firstArchived.BossArt, "Boss art must not leak into archived ordinary stages after rolling retention drops them from the manifest.");
        });
    }

    [TestMethod]
    public void TerminalFailuresAndReceiptsSurviveCorruptionWithoutRestartingTheArtist()
    {
        WithStore(store =>
        {
            var run = store.Create("Failure", 90); store.TrySaveLevel(run.Id, Level());
            var progress = LivingArcadeSceneryArtTests.Progress(store.Load(run.Id)!);
            store.TrySaveBossArt(run.Id, 3, new(run.Seed, 3, "failed", Failure: "InvalidDataException"));
            var path = Path.Combine(store.RootDirectory, run.Id, "boss-art", "3.json"); File.Copy(path, path + ".bak");
            File.WriteAllText(path, "{broken");
            Assert.AreEqual("InvalidDataException", store.GetBossArt(run.Id, 3)!.Failure);
            File.WriteAllText(path + ".bak", "{also broken");
            var terminal = store.GetBossArt(run.Id, 3)!;
            Assert.AreEqual("failed", terminal.Outcome); Assert.AreEqual("UnreadableOptionalReceipt", terminal.Failure);
            Assert.IsTrue(store.TrySaveBossArt(run.Id, 3, terminal));
            Assert.AreEqual("failed", new LivingArcadeRunStore(store.RootDirectory).GetBossArt(run.Id, 3)!.Outcome);
            Assert.IsNull(store.Load(run.Id)!.Levels[3].BossArt);
            Assert.AreEqual(progress, LivingArcadeSceneryArtTests.Progress(store.Load(run.Id)!));
            Assert.ThrowsExactly<InvalidDataException>(() => new LivingArcadeBossArtReceipt(run.Seed + 1, 3, "generated", Art()).Validate(run.Seed, 3));
            Assert.IsTrue(store.Delete(run.Id));
            Assert.IsFalse(store.TrySaveBossArt(run.Id, 3, new(run.Seed, 3, "generated", Art())));
        });
    }

    [TestMethod]
    public void BossCheckpointKeepsConfirmedArtAcrossRestartAndRejectsUnrelatedLevels()
    {
        var receipt = new LivingArcadeBossArtReceipt(19, 3, "generated", Art());
        JsonElement Checkpoint(Dictionary<int, LivingArcadeBossArtReceipt> receipts) => JsonSerializer.SerializeToElement(new
        { RunId = "boss-fixture", CurrentLevel = 2, BossArtLevels = receipts });
        var restored = Restore(Checkpoint(new() { [3] = receipt }));
        Assert.AreEqual("generated", restored.GetProperty("BossArtLevels").GetProperty("3").GetProperty("Outcome").GetString());
        Assert.ThrowsExactly<TargetInvocationException>(() => Restore(Checkpoint(new() { [6] = receipt with { LevelNumber = 6 } })));
        Assert.ThrowsExactly<TargetInvocationException>(() => Restore(Checkpoint(new() { [3] = receipt with { LevelNumber = 6 } })));
        Assert.ThrowsExactly<TargetInvocationException>(() => Restore(Checkpoint(new()
        { [3] = receipt, [6] = receipt with { LevelNumber = 6 }, [9] = receipt with { LevelNumber = 9 } })));
        var level = Level() with { Number = 2, BossArt = Art() };
        Assert.ThrowsExactly<InvalidDataException>(() => LivingArcadeLevelPolicy.Validate(level));
    }

    internal static LivingArcadeBossArt Art() => new()
    {
        TitleRu = "Железный архивариус", TitleEn = "Iron archivist", Source = "model",
        Visual = new() { Mode = "vector", Animation = "sway", Frames = [new() { Shapes = Enumerable.Range(0, 32)
            .Select(index => new LivingArcadeVisualShape { Kind = index % 2 == 0 ? "rect" : "ellipse",
                X = 16 + index % 8 * 27, Y = 30 + index / 8 * 45, Width = 18, Height = 32,
                Fill = index % 2 == 0 ? "#485B75" : "#C17D40", Stroke = "#203044" }).ToList() }] }
    };

    private static LivingArcadeLevel Level() => LivingArcadeLevelPolicy.CreateFirst(90) with { Number = 3, Seed = 703, Authored = false };
    private static string WithoutArt(LivingArcadeLevel level) => JsonSerializer.Serialize(level with { BossArt = null }, LivingArcadeLevelPolicy.JsonOptions);
    private static JsonElement Restore(JsonElement checkpoint)
    {
        var request = new LivingArcadeGenerationRequest("boss-fixture", 2, new("main.gguf", "artist.gguf"), "ru");
        var state = new BackgroundOperationState { Kind = LivingArcadeGeneration.Kind, Title = "Preparing", Checkpoint = checkpoint };
        var result = typeof(LivingArcadeGeneration).GetMethod("ReadCheckpoint", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [state, request])!;
        return JsonSerializer.SerializeToElement(result, result.GetType());
    }
    private static void WithStore(Action<LivingArcadeRunStore> test)
    {
        var directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "arcade-boss-art-" + Guid.NewGuid().ToString("N")));
        try { test(new(directory)); }
        finally
        {
            if (Directory.Exists(directory) && directory.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(directory).StartsWith("arcade-boss-art-", StringComparison.Ordinal)) Directory.Delete(directory, true);
        }
    }
}
