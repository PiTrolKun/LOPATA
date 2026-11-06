using AIHub.Models;

namespace AIHub.Services.LivingArcade;

public sealed partial class LivingArcadeGeneration
{
    internal static object BossArtistResponseFormat(LivingArcadeLevel level, LivingArcadeLibrary library)
    {
        object Enum(params string[] values) => new { type = "string", @enum = values };
        object Num(double min, double max) => new { type = "number", minimum = min, maximum = max };
        object Array(object items, int min, int max) => new { type = "array", items, minItems = min, maxItems = max };
        object Obj(Dictionary<string, object> fields) => new { type = "object", additionalProperties = false,
            properties = fields, required = fields.Keys.ToArray() };
        var color = new { type = "string", pattern = "^#[0-9A-Fa-f]{6}$" };
        var paint = new { type = "string", pattern = "^(#[0-9A-Fa-f]{6}|none)$" };
        var bases = LivingArcadeBossArtCatalog.Shortlist(library, level.TitleEn + " " + level.DescriptionEn, level.Seed);
        var parts = library.Shortlist(level.TitleEn + " " + level.DescriptionEn, level.Seed, 8, charactersOnly: false);
        var layerIds = bases.Select(entry => entry.Id).Concat(parts.Select(entry => entry.Id)).Distinct().ToArray();
        object Shape(bool fitting) => Obj(new()
        {
            ["kind"] = Enum("rect", "ellipse", "line", "path"), ["x"] = Num(0, 256), ["y"] = Num(0, 256),
            ["width"] = Num(fitting ? -96 : -256, fitting ? 96 : 256),
            ["height"] = Num(fitting ? -96 : -256, fitting ? 96 : 256),
            ["data"] = new { type = new[] { "string", "null" }, maxLength = 4096 }, ["fill"] = paint,
            ["stroke"] = paint, ["strokeWidth"] = Num(0, 16), ["opacity"] = Num(0, 1)
        });
        var frame = Obj(new() { ["seconds"] = Num(.04, 2), ["shapes"] = Array(Shape(false), 24, 128) });
        object Layer(bool fitting) => Obj(new()
        {
            ["assetId"] = layerIds.Length == 0 ? new { type = "string", maxLength = 0 } : Enum(layerIds),
            ["x"] = Num(0, 256), ["y"] = Num(0, 256),
            ["width"] = Num(1, fitting ? 96 : 256), ["height"] = Num(1, fitting ? 96 : 256), ["rotation"] = Num(-30, 30),
            ["opacity"] = Num(0, 1), ["animation"] = Enum("none", "bob", "sway", "pulse", "cycle")
        });
        var swap = Obj(new() { ["from"] = color, ["to"] = color });
        object Visual(string mode) => Obj(new()
        {
            ["mode"] = Enum(mode), ["assetId"] = mode == "library" ? Enum(bases.Select(entry => entry.Id).ToArray()) : new { type = "null" },
            ["pixelArt"] = new { type = "boolean" }, ["animation"] = Enum("none", "bob", "sway", "pulse", "cycle"),
            ["animationAmount"] = Num(0, 15), ["animationPeriod"] = Num(.5, 4),
            ["palette"] = Array(swap, 0, mode == "library" ? 8 : 0),
            ["frames"] = Array(frame, mode == "vector" ? 1 : 0, mode == "vector" ? 3 : 0),
            ["decorations"] = Array(Shape(mode == "library"), mode == "library" ? 4 : 0, 16),
            ["layers"] = Array(Layer(mode == "library"), 0, layerIds.Length == 0 ? 0 : 8)
        });
        var visualModes = bases.Count == 0 ? new object[] { Visual("vector") }
            : new object[] { Visual("library"), Visual("vector") };
        return new { type = "json_schema", json_schema = new { name = "arcade_boss_art", strict = true,
            schema = Obj(new()
            {
                ["titleRu"] = new { type = "string", minLength = 1, maxLength = 100 },
                ["titleEn"] = new { type = "string", minLength = 1, maxLength = 100 },
                ["visual"] = new { anyOf = visualModes }
            }) } };
    }
}
