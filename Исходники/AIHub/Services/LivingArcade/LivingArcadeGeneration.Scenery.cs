using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.LivingArcade;

public sealed partial class LivingArcadeGeneration
{
    private async Task<LivingArcadeChapterScenery> PrepareOptionalSceneryAsync(LivingArcadeRunStore store,
        string runId, int levelNumber, LivingArcadeModelPaths paths, LivingArcadeChapterScenery? checkpoint,
        Action<LivingArcadeChapterScenery> save, CancellationToken ct)
    {
        var run = RequireRun(store, runId);
        var chapter = LivingArcadeSceneryArtPolicy.ChapterFor(levelNumber);
        var confirmed = store.GetChapterScenery(runId, levelNumber) ?? checkpoint;
        if (confirmed is not null)
        {
            confirmed.Validate(run.Seed, chapter);
            save(confirmed);
            if (store.TrySaveChapterScenery(runId, levelNumber, confirmed)) RunUpdated?.Invoke(runId);
            return confirmed;
        }
        LivingArcadeChapterScenery result;
        if (!LivingArcadeSceneryArtPolicy.ShouldGenerate(run.Seed, chapter))
            result = new(run.Seed, chapter, "builtin");
        else
        {
            Status("Drawing");
            try
            {
                var art = await AskAsync(paths, true, SceneryPrompt, SceneryInput(run, chapter), 7500,
                    ParseGeneratedScenery, ct, repairs: 0, responseFormat: SceneryResponseFormat());
                result = new(run.Seed, chapter, "generated", art);
            }
            catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
            {
                // Optional decorative art has one bounded attempt. A bad drawing must never block a playable level.
                Trace.TraceWarning("LivingArcade optional scenery fell back to built-in art: {0}", error.GetType().Name);
                result = new(run.Seed, chapter, "failed", Failure: error.GetType().Name);
                try { await RetireAsync(); }
                catch (Exception cleanup) when (cleanup is not OperationCanceledException and not OutOfMemoryException)
                { Trace.TraceWarning("LivingArcade scenery runtime cleanup: {0}", cleanup.GetType().Name); }
            }
        }
        save(result); // Persist success, intentional built-in choice, or terminal failure before any later model step.
        ct.ThrowIfCancellationRequested();
        if (store.TrySaveChapterScenery(runId, levelNumber, result)) RunUpdated?.Invoke(runId);
        return result;
    }

    private static string SceneryInput(LivingArcadeRun run, int chapter) => JsonSerializer.Serialize(new
    {
        runSeed = run.Seed, chapter, firstLevel = (long)chapter * 3 + 1,
        chapterLevels = run.Levels.Values.Where(level => LivingArcadeSceneryArtPolicy.ChapterFor(level.Number) == chapter)
            .Select(level => new { level.TitleEn, level.DescriptionEn }).ToArray(),
        previousLocations = run.Levels.Values.Where(level => LivingArcadeSceneryArtPolicy.ChapterFor(level.Number) != chapter
                && level.Scenery is not null).Select(level => level.Scenery!.TitleEn).Distinct().TakeLast(3).ToArray(),
        purpose = "An original place for three arcade stages. Invent your own architecture, vegetation, machinery or surreal landmarks; not a selection from a fixed template list."
    }, CompactPlanOptions);

    internal static LivingArcadeGeneratedScenery ParseGeneratedScenery(string json)
    {
        using (var document = JsonDocument.Parse(json))
            foreach (var field in new[] { "titleRu", "titleEn", "skyColor", "horizonColor", "groundColor", "roadColor", "markColor",
                "horizonShapes", "groundShapes", "roadsideObjects" })
                if (!document.RootElement.TryGetProperty(field, out _)) throw new InvalidDataException("Arcade.Ai.InvalidScenery");
        var art = Object<LivingArcadeGeneratedScenery>(json);
        art.Credits = [new("chapter scenery", "Qwen/Qwen2.5-Coder-3B-Instruct-GGUF", LivingArcadeModels.ArtistRevision)];
        art.CreatedUtc = DateTimeOffset.UtcNow;
        LivingArcadeSceneryGeometryNormalizer.Fit(art);
        LivingArcadeSceneryArtPolicy.Validate(art);
        return art;
    }

    internal static object SceneryResponseFormat()
    {
        object Num(double min, double max) => new { type = "number", minimum = min, maximum = max };
        object Array(object items, int min, int max) => new { type = "array", items, minItems = min, maxItems = max };
        object Obj(Dictionary<string, object> fields) => new { type = "object", additionalProperties = false,
            properties = fields, required = fields.Keys.ToArray() };
        var color = new { type = "string", pattern = "^#[0-9A-Fa-f]{6}$" };
        var paint = new { type = "string", pattern = "^(#[0-9A-Fa-f]{6}|none)$" };
        var text = new { type = "string", minLength = 1, maxLength = 100 };
        var shape = Obj(new()
        {
            ["kind"] = new { type = "string", @enum = new[] { "rect", "ellipse", "line", "path" } },
            ["x"] = Num(0, 256), ["y"] = Num(0, 256), ["width"] = Num(-256, 256), ["height"] = Num(-256, 256),
            ["data"] = new { type = new[] { "string", "null" }, maxLength = 4096 },
            ["fill"] = paint, ["stroke"] = paint, ["strokeWidth"] = Num(0, 16), ["opacity"] = Num(0, 1)
        });
        return new { type = "json_schema", json_schema = new { name = "arcade_chapter_scenery", strict = true,
            schema = Obj(new()
            {
                ["titleRu"] = text, ["titleEn"] = text, ["skyColor"] = color, ["horizonColor"] = color,
                ["groundColor"] = color, ["roadColor"] = color, ["markColor"] = color,
                ["horizonShapes"] = Array(shape, 1, 256), ["groundShapes"] = Array(shape, 0, 128),
                ["roadsideObjects"] = Array(Obj(new() { ["name"] = text, ["shapes"] = Array(shape, 1, 128) }), 1, 6)
            }) } };
    }

    private const string SceneryPrompt = """
        You create original decorative scenery for an arcade game. Return ONLY the requested JSON, no code or SVG/XML.
        Invent a coherent new place and detailed recognizable landmarks, not just colored rectangles or geometric placeholders.
        The location stays the same for three stages and changes after a boss. Make it clearly different from previousLocations.
        You may invent wild, imperfect or surreal art. You are not restricted to six themes or a list of ready-made buildings.
        Make 3 to 5 varied roadsideObjects, each with a name and its own compound drawing: structural silhouette, parts,
        material details, shadows and highlights. Invent a distinctive original location and your own objects for it.
        Each object uses a 256x256 local canvas; its base is near y=256 and center x=128. Keep the complete object inside it.
        horizonShapes draw a distant skyline on another 256-square canvas. groundShapes draw a repeatable flat floor tile,
        with muted seams, small stones or tracks. Decorative objects never become obstacles or change collision or enemy behavior.
        Fields: titleRu,titleEn,skyColor,horizonColor,groundColor,roadColor,markColor,horizonShapes,groundShapes,roadsideObjects.
        Colors are #RRGGBB. Keep roadColor dark/muted so bullets and enemies remain readable; roadside objects can be colorful.
        Each object is {name,shapes}; names may describe any invented object, not a fixed enum.
        Each shape is {kind,x,y,width,height,data,fill,stroke,strokeWidth,opacity}.
        kind rect/ellipse/line/path; fill and stroke #RRGGBB or none; opacity0..1; strokeWidth0..16.
        Coordinates0..256. Rect/ellipse positivewidth/height and x+width<=256,y+height<=256.
        Line width/height are endpoint offsets, possibly negative, but both endpoints stay inside0..256.
        data=null unless path. For a path set x=0,y=0,width=0,height=0: its data already contains all positions.
        Path data uses local M/L/C/Q/Z geometry coordinates inside0..256, not shared world coordinates.
        No XML/SVG, URLs, files or code. A road object has its own complete local canvas even if placed far along the road.
        A modest complete answer is better than truncation: aim for8..16 meaningful shapes per landmark,
        5..12 horizon shapes,3..8 ground shapes. Hard caps256 horizon,128 ground,6 landmarks with128 shapes each.
        Do not include credits, timestamps or any gameplay fields; the host provides provenance.
        """;
}
