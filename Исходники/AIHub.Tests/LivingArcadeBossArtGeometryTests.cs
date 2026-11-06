using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services.LivingArcade;

namespace AIHub.Tests;

[TestClass]
public sealed class LivingArcadeBossArtGeometryTests
{
    [TestMethod]
    public void OnlyInactiveZeroAnimationPeriodDefaultsAndTheArtistCannotClaimIt()
    {
        foreach (var mode in new[] { "none", "cycle" })
        {
            var art = LivingArcadeBossArtTests.Art(); art.Visual.Animation = mode; art.Visual.AnimationPeriod = 0;
            Assert.ThrowsExactly<InvalidDataException>(() => LivingArcadeVisualPolicy.Validate(art.Visual));
            var parsed = Parse(art);
            Assert.AreEqual(1.5, parsed.Visual.AnimationPeriod); Assert.IsTrue(parsed.InactiveAnimationDefaulted);
            LivingArcadeVisualPolicy.Validate(parsed.Visual);
        }
        var moving = LivingArcadeBossArtTests.Art(); moving.Visual.Animation = "sway"; moving.Visual.AnimationPeriod = 0;
        Assert.ThrowsExactly<InvalidDataException>(() => Parse(moving));
        var valid = LivingArcadeBossArtTests.Art(); valid.InactiveAnimationDefaulted = true;
        Assert.IsFalse(Parse(valid).InactiveAnimationDefaulted, "Only the host may record a default that actually happened.");
    }

    [TestMethod]
    public void FiniteVectorCanvasIsFittedTogetherWithoutRelaxingCommonVisualPolicy()
    {
        var art = LivingArcadeBossArtTests.Art();
        foreach (var shape in art.Visual.Frames[0].Shapes)
        { shape.X *= 2; shape.Y *= 2; shape.Width *= 2; shape.Height *= 2; }
        Assert.ThrowsExactly<InvalidDataException>(() => LivingArcadeVisualPolicy.Validate(art.Visual));
        var parsed = Parse(art);
        Assert.IsTrue(parsed.GeometryFitted); Assert.IsFalse(parsed.PaletteDeduplicated);
        Assert.AreEqual(32, parsed.Visual.Frames[0].Shapes.Count);
        var xScale = parsed.Visual.Frames[0].Shapes[0].Width / art.Visual.Frames[0].Shapes[0].Width;
        foreach (var shape in parsed.Visual.Frames[0].Shapes)
            Assert.AreEqual(xScale, shape.Width / 36, 1e-8, "One affine transform must preserve all relative part sizes.");
        LivingArcadeVisualPolicy.Validate(parsed.Visual);
        var valid = LivingArcadeBossArtTests.Art(); valid.GeometryFitted = true; valid.PaletteDeduplicated = true;
        var unmodified = Parse(valid);
        Assert.IsFalse(unmodified.GeometryFitted); Assert.IsFalse(unmodified.PaletteDeduplicated,
            "The artist cannot claim a host correction that did not take place.");
        var rotated = LivingArcadeBossArtTests.Art();
        rotated.Visual.Layers = [new() { AssetId = LivingArcadeBossArtCatalog.Shortlist(LivingArcadeLibrary.Load(), "", 703).First().Id,
            X = 128, Y = 128, Width = 256, Height = 256, Rotation = 30 }];
        var rotatedFit = Parse(rotated);
        Assert.IsTrue(rotatedFit.GeometryFitted);
        LivingArcadeVisualPolicy.Validate(rotatedFit.Visual);
    }

    [TestMethod]
    public void SmallLibraryFittingsFitAndIdenticalPaletteRepeatsAreRecordedHonestly()
    {
        var art = LibraryArt();
        art.Visual.Decorations.Add(new() { Kind = "path", X = 128, Y = 128, Width = 256, Height = 256,
            Data = "M238,50L270,50L270,64L238,64Z", Fill = "#D6A34C" });
        art.Visual.Layers = [new() { AssetId = art.Visual.AssetId!, X = 240, Y = 240, Width = 40, Height = 32, Rotation = 30 }];
        art.Visual.Palette = [new() { From = "#FFDD8C", To = "#F0D184" }, new() { From = "#ffdd8c", To = "#f0d184" }];
        Assert.ThrowsExactly<InvalidDataException>(() => LivingArcadeVisualPolicy.Validate(art.Visual));
        var parsed = Parse(art);
        Assert.IsTrue(parsed.GeometryFitted); Assert.IsTrue(parsed.PaletteDeduplicated);
        Assert.AreEqual(1, parsed.Visual.Palette.Count);
        Assert.AreEqual(0, parsed.Visual.Decorations[^1].X); Assert.AreEqual(0, parsed.Visual.Decorations[^1].Width);
        Assert.AreEqual(40d, parsed.Visual.Layers.Single().Width); Assert.AreEqual(32d, parsed.Visual.Layers.Single().Height);
        Assert.AreEqual("model", parsed.Source);
        Assert.AreEqual(art.Visual.AssetId, parsed.AssetCredits.Single().AssetId);
        LivingArcadeVisualPolicy.Validate(parsed.Visual);
        LivingArcadeBossArtPolicy.ValidateGeneratedDetail(parsed, LivingArcadeLibrary.Load(),
            LivingArcadeBossArtCatalog.Shortlist(LivingArcadeLibrary.Load(), Level().TitleEn + " " + Level().DescriptionEn, Level().Seed));
    }

    [TestMethod]
    public void NativeGiantOverlayIsNotLaunderedIntoAValidLibraryBoss()
    {
        var art = LibraryArt(); art.Visual.AssetId = "glitch-npc-crab";
        art.Visual.Decorations = [new() { Kind = "path", X = 128, Y = 128, Width = 256, Height = 256,
            Data = "M128 128L240 128C320 128 400 128 480 128L480 240C480 320 480 400 480 480L320 480C240 480 128 480 128 320L128 240C128 128 128 128 128 128Z",
            Fill = "#FFDD8C", Stroke = "#FFDD8C", StrokeWidth = 4, Opacity = .5 }];
        art.Visual.Layers = [new() { AssetId = "kenney-purple-body-rhombus", X = 128, Y = 128,
            Width = 256, Height = 256, Rotation = -30, Opacity = 1 }];
        var failure = Assert.ThrowsExactly<InvalidDataException>(() => Parse(art));
        StringAssert.Contains((string)failure.Data["ValidationDetail"]!, "giant opaque");
        art = LibraryArt(); art.Visual.Layers = [new() { AssetId = art.Visual.AssetId!, Width = 256, Height = 256 }];
        Assert.ThrowsExactly<InvalidDataException>(() => Parse(art), "A full-size opaque attachment still obscures the detailed base.");
        art = LibraryArt(); art.Visual.Decorations = Enumerable.Range(0, 16).Select(index => new LivingArcadeVisualShape
        { Kind = "rect", X = index, Y = index, Width = 96, Height = 96, Fill = "#AABBCC" }).ToList();
        Assert.ThrowsExactly<InvalidDataException>(() => Parse(art), "Many small plates cannot collectively hide the original illustration either.");
    }

    [TestMethod]
    public void FittingDoesNotAdmitUnboundedGeometryOrConflictingPalette()
    {
        var art = LivingArcadeBossArtTests.Art(); art.Visual.Frames[0].Shapes[0].X = 999;
        Assert.ThrowsExactly<InvalidDataException>(() => Parse(art));
        art = LibraryArt(); art.Visual.Decorations[0] = new() { Kind = "path", Data = "M0,0L999,0L999,5Z", Fill = "#AABBCC" };
        Assert.ThrowsExactly<InvalidDataException>(() => Parse(art));
        art = LibraryArt(); art.Visual.Palette = [new() { From = "#FFDD8C", To = "#112233" }, new() { From = "#FFDD8C", To = "#445566" }];
        var failure = Assert.ThrowsExactly<InvalidDataException>(() => Parse(art));
        StringAssert.Contains((string)failure.Data["ValidationDetail"]!, "conflicting");
        art = LibraryArt(); art.Visual.Decorations.Clear();
        var sparse = Assert.ThrowsExactly<InvalidDataException>(() => Parse(art));
        StringAssert.Contains((string)sparse.Data["ValidationDetail"]!, "4 to 16 distinct small");
    }

    [TestMethod]
    public void LibrarySchemaRequiresSmallFittingsWhileOriginalBodiesRemainRich()
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(LivingArcadeGeneration.BossArtistResponseFormat(Level(), LivingArcadeLibrary.Load())));
        var modes = document.RootElement.GetProperty("json_schema").GetProperty("schema").GetProperty("properties").GetProperty("visual").GetProperty("anyOf");
        var library = modes.EnumerateArray().Single(mode => mode.GetProperty("properties").GetProperty("mode").GetProperty("enum")[0].GetString() == "library").GetProperty("properties");
        Assert.AreEqual(4, library.GetProperty("decorations").GetProperty("minItems").GetInt32());
        Assert.AreEqual(96, library.GetProperty("decorations").GetProperty("items").GetProperty("properties").GetProperty("width").GetProperty("maximum").GetInt32());
        Assert.AreEqual(96, library.GetProperty("layers").GetProperty("items").GetProperty("properties").GetProperty("width").GetProperty("maximum").GetInt32());
        var vector = modes.EnumerateArray().Single(mode => mode.GetProperty("properties").GetProperty("mode").GetProperty("enum")[0].GetString() == "vector").GetProperty("properties");
        Assert.AreEqual(128, vector.GetProperty("frames").GetProperty("items").GetProperty("properties").GetProperty("shapes").GetProperty("maxItems").GetInt32());
    }

    private static LivingArcadeBossArt LibraryArt()
    {
        var level = Level(); var basis = LivingArcadeBossArtCatalog.Shortlist(LivingArcadeLibrary.Load(), level.TitleEn + " " + level.DescriptionEn, level.Seed).First();
        var art = LivingArcadeBossArtTests.Art();
        art.Visual = new() { Mode = "library", AssetId = basis.Id, Animation = "sway",
            Decorations = LivingArcadeBossArtTests.Art().Visual.Frames[0].Shapes.Take(4).ToList() };
        return art;
    }
    private static LivingArcadeBossArt Parse(LivingArcadeBossArt art) => LivingArcadeGeneration.ParseGeneratedBossArt(
        JsonSerializer.Serialize(art, LivingArcadeLevelPolicy.JsonOptions), Level(), LivingArcadeLibrary.Load());
    private static LivingArcadeLevel Level() => LivingArcadeLevelPolicy.CreateFirst(90) with { Number = 3, Seed = 703, Authored = false };
}
