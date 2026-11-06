using System.Security.Cryptography;
using System.Text;
using AIHub.Models;

namespace AIHub.Services.LivingArcade;

/// <summary>Detailed packaged characters for bosses; selection does not change their combat geometry.</summary>
internal static class LivingArcadeBossArtCatalog
{
    private sealed record Candidate(string Id, string[] Themes);
    // These are reviewed prepared characters, not unrendered SVG reference layers or atomic Kenney parts.
    private static readonly Candidate[] Detailed =
    [
        new("glitch-npc-crab", ["crab", "animal", "sea", "harbor", "water", "краб", "море", "порт"]),
        new("glitch-npc-batterfly", ["bat", "flying", "forest", "wing", "cave", "лес", "крылья", "пещера"]),
        new("glitch-street-spirit-groddle", ["spirit", "magic", "forest", "mushroom", "дух", "магия", "лес"]),
        new("glitch-street-spirit", ["spirit", "creature", "forest", "дух", "существо", "лес"]),
        new("glitch-street-spirit-ix", ["spirit", "alien", "ice", "crystal", "дух", "лёд", "кристалл"]),
        new("glitch-npc-forehorseman", ["horse", "animal", "desert", "rider", "конь", "животное", "пустыня"]),
        new("cartoon-robot", ["robot", "mech", "machine", "factory", "робот", "механизм", "завод"]),
        new("cartoon-space-armor", ["space", "soldier", "armor", "machine", "космос", "солдат", "броня"]),
        new("cartoon-space-armor-dark", ["space", "soldier", "armor", "city", "космос", "солдат", "броня", "город"]),
        new("cartoon-stone-giant", ["stone", "giant", "rock", "desert", "камень", "великан", "скала"]),
        new("cartoon-orc", ["orc", "warrior", "monster", "fortress", "орк", "воин", "крепость"]),
        new("cartoon-dwarf-warrior", ["dwarf", "warrior", "armor", "fortress", "гном", "воин", "броня", "крепость"])
    ];

    internal static IReadOnlyList<LivingArcadeLibraryEntry> Shortlist(LivingArcadeLibrary library, string concept, int seed)
    {
        var terms = (concept ?? "").ToLowerInvariant().Split(
            [' ', ',', '.', ';', ':', '-', '_', '\n', '\r', '/', '\\', '(', ')'], StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var available = library.Entries.Where(entry => entry.Role == "character" && entry.Frames.Length > 0).ToArray();
        var curated = Detailed.ToDictionary(item => item.Id, StringComparer.Ordinal);
        return available.OrderByDescending(entry => curated.ContainsKey(entry.Id))
            .ThenByDescending(entry => (curated.TryGetValue(entry.Id, out var candidate) ? candidate.Themes : [])
                .Concat(entry.Tags).Distinct(StringComparer.OrdinalIgnoreCase).Count(term => terms.Contains(term.ToLowerInvariant())))
            .ThenBy(entry => Order(entry.Id, seed), StringComparer.Ordinal).Take(12).ToArray();
    }

    internal static LivingArcadeBossArt CreateFallback(LivingArcadeLevel level, LivingArcadeLibrary library)
    {
        var selectionSeed = unchecked(level.Seed * 397 ^ level.Number * 7919);
        var concept = level.TitleEn + " " + level.DescriptionEn + " " + level.TitleRu + " " + string.Join(' ', level.Mechanics);
        var entry = Shortlist(library, concept, selectionSeed).FirstOrDefault();
        if (entry is null) return AuthoredFallback(level);
        var pack = library.Packs.Single(item => item.Id == entry.PackId);
        var models = (entry.Origin?.Models ?? []).Select(model =>
            new LivingArcadeModelCredit(model.Role, model.Id, model.Revision)).ToList();
        var visual = new LivingArcadeVisual
        {
            Mode = "library", AssetId = entry.Id,
            Animation = entry.Frames.Length > 1 ? "cycle" : entry.Tags.Contains("flying") ? "bob" : "sway",
            AnimationAmount = 3, AnimationPeriod = 2,
            Decorations = Regalia()
        };
        LivingArcadeVisualPolicy.Validate(visual);
        return new()
        {
            TitleRu = "Страж главы " + ((level.Number + 2L) / 3), TitleEn = entry.Title + " guardian",
            Visual = visual, Source = "library-fallback", Credits = models,
            AssetCredits = [new(entry.Id, library.AuditRevision, entry.PackId, pack.Author,
                pack.License, pack.SourceUrl, models.Select(model => model with { }).ToList())]
        };
    }

    private static string Order(string id, int seed) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(seed + ":" + id)));

    private static List<LivingArcadeVisualShape> Regalia()
    {
        var shapes = new List<LivingArcadeVisualShape>
        {
            new() { Kind = "path", Data = "M105,19L101,6L114,12L128,2L142,12L155,6L151,19Z",
                Fill = "#E8B957", Stroke = "#473B30", StrokeWidth = 1.5 },
            new() { Kind = "ellipse", X = 124, Y = 9, Width = 8, Height = 8, Fill = "#FFF4B0" }
        };
        foreach (var x in new[] { 8d, 236d })
        {
            shapes.Add(Rect(x, 198, 12, 3, "#E8B957"));
            shapes.Add(Rect(x, 205, 12, 3, "#72CBD9"));
            shapes.Add(Rect(x, 212, 12, 3, "#E8B957"));
            shapes.Add(new() { Kind = "ellipse", X = x + 3, Y = 220, Width = 6, Height = 6, Fill = "#FFF4B0" });
        }
        return shapes;
    }

    private static LivingArcadeBossArt AuthoredFallback(LivingArcadeLevel level)
    {
        // An isolated/custom test library can have no renderable characters. Keep a recognizable compound sentinel.
        var shapes = new List<LivingArcadeVisualShape>
        {
            Rect(65, 62, 126, 122, "#1B273E"), Rect(74, 70, 108, 104, "#6A809B"),
            Rect(86, 82, 84, 82, "#293D58"), Rect(95, 89, 66, 65, "#111D31"),
            Rect(91, 22, 74, 48, "#26364B"), Rect(99, 29, 58, 34, "#859AB0"),
            Rect(103, 36, 50, 12, "#101B2A"), Rect(108, 39, 15, 5, "#66F0E5"),
            Rect(133, 39, 15, 5, "#66F0E5"), Rect(117, 54, 23, 4, "#33465E"),
            Rect(43, 70, 26, 86, "#425A78"), Rect(187, 70, 26, 86, "#425A78"),
            Rect(28, 126, 38, 34, "#25374E"), Rect(190, 126, 38, 34, "#25374E"),
            Rect(34, 110, 12, 40, "#8698AE"), Rect(210, 110, 12, 40, "#8698AE"),
            Rect(79, 178, 36, 50, "#26384E"), Rect(141, 178, 36, 50, "#26384E"),
            Rect(71, 220, 49, 14, "#7389A4"), Rect(136, 220, 49, 14, "#7389A4"),
            new() { Kind = "ellipse", X = 106, Y = 99, Width = 44, Height = 44, Fill = "#0C1729", Stroke = "#C0D2DB", StrokeWidth = 4 },
            new() { Kind = "ellipse", X = 115, Y = 108, Width = 26, Height = 26, Fill = "#64E5DE", Stroke = "#2A949E", StrokeWidth = 3 }
        };
        foreach (var x in new[] { 47d, 191d })
        for (var index = 0; index < 5; index++) shapes.Add(Rect(x, 78 + index * 12, 18, 5, "#A7BAC8"));
        foreach (var x in new[] { 78d, 171d })
        for (var index = 0; index < 5; index++)
            shapes.Add(new() { Kind = "ellipse", X = x, Y = 76 + index * 21, Width = 6, Height = 6,
                Fill = "#19273B", Stroke = "#CCD5DA", StrokeWidth = 1 });
        foreach (var x in new[] { 85d, 147d })
        for (var index = 0; index < 4; index++) shapes.Add(Rect(x, 184 + index * 9, 24, 4, "#96AFC5"));
        for (var index = 0; index < 5; index++) shapes.Add(Rect(99 + index * 12, 157, 7, 8, "#CE9A49"));
        shapes.AddRange(Regalia());
        var visual = new LivingArcadeVisual
        {
            Mode = "vector", Animation = "bob", AnimationAmount = 2, AnimationPeriod = 2,
            Frames = [new() { Seconds = .2, Shapes = shapes }]
        };
        LivingArcadeVisualPolicy.Validate(visual);
        return new()
        {
            TitleRu = "Бронированный страж", TitleEn = "Armored sentinel", Visual = visual,
            Source = "authored-fallback", Credits = [], AssetCredits = []
        };
    }

    private static LivingArcadeVisualShape Rect(double x, double y, double width, double height, string fill) =>
        new() { Kind = "rect", X = x, Y = y, Width = width, Height = height, Fill = fill };
}
