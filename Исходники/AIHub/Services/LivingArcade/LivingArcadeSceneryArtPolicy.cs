using System.Globalization;
using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.LivingArcade;

/// <summary>Finite artistic data and a stable 1-in-8 built-in alternative, separate from combat policy.</summary>
public static class LivingArcadeSceneryArtPolicy
{
    public const int MaximumSerializedLength = 200_000;
    public const int MaximumPathLength = 65_536;
    public static int ChapterFor(int levelNumber) => (Math.Max(1, levelNumber) - 1) / 3;

    public static bool ShouldGenerate(int runSeed, int chapter)
    {
        var value = unchecked((uint)runSeed ^ (uint)Math.Max(0, chapter) * 0x9E3779B9u ^ 0xA24BAED5u);
        unchecked
        {
            value ^= value >> 16; value *= 0x7FEB352Du;
            value ^= value >> 15; value *= 0x846CA68Bu; value ^= value >> 16;
        }
        return (value & 7) != 0;
    }

    public static void Validate(LivingArcadeGeneratedScenery? art)
    {
        if (art is null) return;
        if (!Text(art.TitleRu) || !Text(art.TitleEn)
            || !Color(art.SkyColor) || !Color(art.HorizonColor) || !Color(art.GroundColor)
            || !Color(art.RoadColor) || !Color(art.MarkColor)
            || art.HorizonShapes is null || art.HorizonShapes.Count is < 1 or > 256
            || art.GroundShapes is null || art.GroundShapes.Count > 128
            || art.RoadsideObjects is null || art.RoadsideObjects.Count is < 1 or > 6
            || art.Credits is null || art.Credits.Count > 8
            || art.Credits.Any(credit => credit is null || string.IsNullOrWhiteSpace(credit.Role) || credit.Role.Length > 100
                || string.IsNullOrWhiteSpace(credit.ModelId) || credit.ModelId.Length > 200
                || string.IsNullOrWhiteSpace(credit.Revision) || credit.Revision.Length > 200)) throw Invalid();
        var all = new List<LivingArcadeVisualShape>(art.HorizonShapes);
        all.AddRange(art.GroundShapes);
        foreach (var landmark in art.RoadsideObjects)
        {
            if (landmark is null || !Text(landmark.Name) || landmark.Shapes is null
                || landmark.Shapes.Count is < 1 or > 128) throw Invalid();
            all.AddRange(landmark.Shapes);
        }
        if (all.Any(shape => shape is null) || all.Sum(shape => (long)(shape.Data?.Length ?? 0)) > MaximumPathLength) throw Invalid();
        // Reuse exactly the existing finite geometry rules; each chunk stays inside its visual-frame budget.
        foreach (var chunk in all.Chunk(128))
            LivingArcadeVisualPolicy.Validate(new()
            {
                Mode = "vector", Frames = [new() { Shapes = chunk.ToList() }]
            });
        if (JsonSerializer.Serialize(art, LivingArcadeLevelPolicy.JsonOptions).Length > MaximumSerializedLength) throw Invalid();
    }

    public static LivingArcadeGeneratedScenery? Copy(LivingArcadeGeneratedScenery? art)
    {
        if (art is null) return null;
        Validate(art);
        return JsonSerializer.Deserialize<LivingArcadeGeneratedScenery>(
            JsonSerializer.Serialize(art, LivingArcadeLevelPolicy.JsonOptions), LivingArcadeLevelPolicy.JsonOptions)!;
    }

    private static bool Text(string? text) => !string.IsNullOrWhiteSpace(text) && text.Length <= 100 && !text.Any(char.IsControl);
    private static bool Color(string? value) => value is { Length: 7 } && value[0] == '#'
        && uint.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _);
    private static InvalidDataException Invalid() => new("Arcade.Ai.InvalidScenery");
}

/// <summary>A terminal result, including built-in choice or failed optional art, survives restarts without rerolls.</summary>
internal sealed record LivingArcadeChapterScenery(int RunSeed, int Chapter, string Outcome,
    LivingArcadeGeneratedScenery? Artwork = null, string? Failure = null)
{
    public void Validate(int seed, int chapter)
    {
        if (RunSeed != seed || Chapter != chapter || Chapter < 0 || Outcome is not ("generated" or "builtin" or "failed")
            || (Outcome == "generated") != (Artwork is not null) || Failure?.Length > 200) throw new InvalidDataException("Arcade.Ai.InvalidScenery");
        LivingArcadeSceneryArtPolicy.Validate(Artwork);
    }
}
