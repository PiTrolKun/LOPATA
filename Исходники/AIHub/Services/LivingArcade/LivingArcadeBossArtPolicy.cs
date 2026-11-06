using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.LivingArcade;

/// <summary>Boss art is richer than ordinary actors, but uses the same bounded drawing language.</summary>
public static class LivingArcadeBossArtPolicy
{
    public static void Validate(LivingArcadeBossArt? art)
    {
        if (art is null) return;
        if (string.IsNullOrWhiteSpace(art.TitleRu) || art.TitleRu.Length > 100
            || string.IsNullOrWhiteSpace(art.TitleEn) || art.TitleEn.Length > 100
            || art.Source is not ("model" or "library-fallback" or "authored-fallback")
            || art.Visual is null || art.Visual.Mode is not ("library" or "vector")
            || art.Credits is null || art.Credits.Count > 8 || art.AssetCredits is null || art.AssetCredits.Count > 16)
            throw Invalid();
        LivingArcadeVisualPolicy.Validate(art.Visual);
        if (art.Source == "model" && art.Visual.Mode == "library") ValidateLibraryReadability(art.Visual);
        if (art.Credits.Any(credit => credit is null || !Text(credit.Role, 100)
                || !Text(credit.ModelId, 200) || !Text(credit.Revision, 200))
            || art.AssetCredits.Any(credit => credit is null || !Text(credit.AssetId, 160)
                || !Text(credit.LibraryRevision, 300) || !Text(credit.PackId, 160)
                || !Text(credit.Author, 300) || !Text(credit.License, 200) || !Text(credit.SourceUrl, 2000)
                || credit.Models is null || credit.Models.Count > 8)) throw Invalid();
        if (JsonSerializer.Serialize(art).Length > 200_000) throw Invalid();
    }

    internal static void ValidateGeneratedDetail(LivingArcadeBossArt art, LivingArcadeLibrary library,
        IReadOnlyList<LivingArcadeLibraryEntry> shortlist)
    {
        Validate(art);
        var visual = art.Visual;
        if (visual.Mode == "vector")
        {
            // Every animation frame must be a complete detailed body, not a detailed frame hidden among blanks.
            if (visual.Frames.Count is < 1 or > 3 || visual.Frames.Any(frame => VisibleUnique(frame.Shapes) < 24))
                throw DetailInvalid("Draw a complete boss with at least 24 distinct visible shapes in every frame; aim for 48 to 96. Repeated, transparent or microscopic copies do not add detail.");
        }
        else
        {
            if (!shortlist.Any(entry => entry.Id == visual.AssetId && entry.Role == "character"))
                throw DetailInvalid("Choose a detailed character assetId from the supplied boss shortlist.");
            if (VisibleUnique(visual.Decorations) < 4 && !visual.Layers.Any(layer => layer.Opacity >= .15
                    && layer.Width >= 12 && layer.Height >= 12))
                throw DetailInvalid("Keep a supplied detailed library body. Add 4 to 16 distinct small fittings such as armor, eyes, rivets, lamps or ornaments, preferably 8 to 12. Example: rect x=104,y=32,width=48,height=12; ellipse x=112,y=48,width=8,height=8. Each shape is a small addition, not a replacement body. Use top-left coordinates and preserve the original silhouette.");
        }
        foreach (var layer in visual.Layers)
            if (library.Find(layer.AssetId) is not { Frames.Length: > 0 })
                throw DetailInvalid("Each added layer must reference a prepared assetId from the supplied catalog.");
    }

    internal static void ValidateLibraryReadability(LivingArcadeVisual visual)
    {
        var coveredArea = 0d;
        foreach (var shape in visual.Decorations)
        {
            var bounds = LivingArcadeBossArtGeometryNormalizer.Bounds(shape);
            var filled = shape.Kind != "line" && shape.Fill != "none" && shape.Opacity >= .15;
            if (filled && (bounds.Width > 96 || bounds.Height > 96))
                throw DetailInvalid("A library boss already has a richly detailed body. Do not cover it with one giant opaque shape. Keep filled decorative fittings at most 96x96, normally 6 to 48 pixels; use at least four distinct small parts.");
            if (filled) coveredArea += bounds.Width * bounds.Height * shape.Opacity;
            else if (shape.Stroke != "none") coveredArea += (bounds.Width + bounds.Height) * 2 * shape.StrokeWidth * shape.Opacity;
        }
        foreach (var layer in visual.Layers)
        {
            if (layer is null || !double.IsFinite(layer.Width) || !double.IsFinite(layer.Height)
                || !double.IsFinite(layer.Opacity) || layer.Width > 96 || layer.Height > 96)
                throw DetailInvalid("Catalog layers are small attached parts, at most 96x96. Do not put a full-canvas primitive or another full-size character over the detailed library body.");
            coveredArea += layer.Width * layer.Height * layer.Opacity;
        }
        if (!double.IsFinite(coveredArea) || coveredArea > 256 * 256 * .30)
            throw DetailInvalid("Decorations obscure too much of the illustrated boss. Retain the detailed base: use a few small fitted ornaments, not large overlapping plates. Their combined alpha-weighted bounding area must stay below 30 percent of the canvas.");
    }

    internal static int VisibleUnique(IEnumerable<LivingArcadeVisualShape> shapes) => shapes.Where(Visible)
        .Select(shape => JsonSerializer.Serialize(new { shape.Kind, shape.X, shape.Y, shape.Width, shape.Height, shape.Data }))
        .Distinct(StringComparer.Ordinal).Count();

    private static bool Visible(LivingArcadeVisualShape shape)
    {
        if (shape.Opacity < .15 || shape.Fill == "none" && (shape.Stroke == "none" || shape.StrokeWidth < .5)) return false;
        if (shape.Kind == "line" && (shape.Stroke == "none" || shape.StrokeWidth < .5)) return false;
        if (shape.Kind == "path")
        {
            var bounds = System.Windows.Media.Geometry.Parse(shape.Data!).Bounds;
            return bounds.Width >= 2 && bounds.Height >= 2;
        }
        return shape.Kind == "line" ? Math.Abs(shape.Width) + Math.Abs(shape.Height) >= 6
            : shape.Width >= 2 && shape.Height >= 2;
    }

    public static LivingArcadeBossArt? Copy(LivingArcadeBossArt? art)
    {
        Validate(art);
        return art is null ? null : JsonSerializer.Deserialize<LivingArcadeBossArt>(
            JsonSerializer.Serialize(art, LivingArcadeLevelPolicy.JsonOptions), LivingArcadeLevelPolicy.JsonOptions);
    }

    private static bool Text(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum;
    private static InvalidDataException Invalid() => new("Arcade.Ai.InvalidBossArt");
    internal static InvalidDataException DetailInvalid(string detail)
    {
        var error = Invalid(); error.Data["ValidationDetail"] = detail; return error;
    }
}

internal sealed record LivingArcadeBossArtReceipt(int RunSeed, int LevelNumber, string Outcome,
    LivingArcadeBossArt? Artwork = null, string? Failure = null)
{
    internal void Validate(int seed, int level)
    {
        if (RunSeed != seed || LevelNumber != level || level < 3 || level % 3 != 0
            || Outcome is not ("generated" or "failed") || Failure?.Length > 300
            || Outcome == "generated" != (Artwork is not null)
            || Outcome == "failed" && string.IsNullOrWhiteSpace(Failure))
            throw new InvalidDataException("Arcade.Ai.InvalidCheckpoint");
        LivingArcadeBossArtPolicy.Validate(Artwork);
    }
}
