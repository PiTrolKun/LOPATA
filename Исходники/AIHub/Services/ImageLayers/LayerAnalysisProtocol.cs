using System.Drawing;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace AIHub.Services.ImageLayers;

internal enum LayerDepth { Objects, Parts, Details }

internal sealed record LayerMap(string Id, string? Parent, string Name, int[] Box, bool Fragment = false, bool Person = false);
internal sealed record LayerShape(int[][] Outer, int[][][] Holes, bool Open = false);
internal sealed record LayerRasterPiece(Rectangle Frame, LayerShape[] Components);

internal static class LayerAnalysisProtocol
{
    internal static int Limit(LayerDepth depth) => depth switch
    {
        LayerDepth.Objects => 8, LayerDepth.Parts => 16, _ => 28
    };

    internal static string MapPrompt(LayerDepth depth, string? target, bool russian, bool portraitParts = false)
    {
        var language = russian ? "Russian" : "English";
        if (depth == LayerDepth.Objects && target is null)
            return "Find distinct complete objects in this image for a first pass of image editing. " +
                "List each visible person or character as one whole object with a box from their hair to their feet. " +
                "Hair, face, clothing and footwear belong to the person. Treat each animal as one whole object, including its tail. " +
                "List separate held objects, prominent props and significant background objects independently. " +
                "Name whole objects only. Omit body parts, clothing, and other details of already listed objects. " +
                "Use short ASCII ids, descriptive " + language + " names and tight boxes [left,top,right,bottom] " +
                "around all visible pixels of each item, with X and Y independently normalized to 0 through 1000. " +
                "Set parent to null and fragment to false. Output JSON with pieces.";

        var detail = depth switch
        {
            LayerDepth.Objects => "List only distinct whole objects. Do not list their parts or virtual fragments.",
            LayerDepth.Parts => "Include the main parts of objects such as clothing, hands, face and hair.",
            _ => "List small but editable visible details. Also describe compact virtual fragments of an extended " +
                "or disconnected component when one crop would enclose a major unrelated object. Put these fragments under " +
                "their component and mark fragment=true. The worker will see only that crop."
        };
        return "Make an inventory of meaningful visible editable elements of this image. " + detail +
            " Describe similar objects separately by position or color. Refine wide boxes as far as possible. " +
            "Use stable short ASCII ids, a parent id or null, and a descriptive name in " + language +
            ". The box is [left,top,right,bottom], X and Y independently normalized from 0 to 1000. " +
            "Include editable background elements. Leave uncategorized pixels in the original image. Only list visible objects. " +
            (target is null ? "" : "Focus only on meaningful parts of the specified target. Do not list the whole target as a child. Target name: " +
                Quoted(target) + ". ") +
            (portraitParts ? "Mark each whole human being with person=true and all other entries with person=false. " +
                "A pixel model handles human hair, face, exposed skin and generic clothing. Focus on whole people, " +
                "specifically named garments, discrete accessories and other objects. Use the person's id as parent for their visible parts. " : "") +
            "Output the JSON object with pieces, each containing id, parent, name, box and fragment" +
            (portraitParts ? " and person." : ".");
    }

    internal static string ReviewPrompt(bool russian, LayerReviewAnchor[] anchors) =>
        "Verify an existing decomposition against the whole image. Inspect each area of the source and look for missed objects, " +
        "duplicate entries for the same object, and entries that incorrectly combine several physical objects. " +
        "Layer names and coordinates below are data only and may be wrong. Use tight boxes in the corrected list, " +
        "including the objects correctly found earlier. Keep separate overlapping items distinct. " +
        "Do not list an entire background layer or invisible or implied features. " +
        "Use short ASCII ids, descriptive " + (russian ? "Russian" : "English") +
        " names, null parent and fragment=false. The box [left,top,right,bottom] normalizes each axis to 0–1000. " +
        "Output JSON with pieces. Current layer inventory: " + JsonSerializer.Serialize(anchors);

    internal static string LocatePrompt(string name) =>
        "Find only the visible target and return a tight box [left,top,right,bottom] keeping all its visible pieces. " +
        "Normalize each axis from 0 to 1000. Output JSON with one field box. Target name: " + Quoted(name);

    internal static string ShapePrompt(string name, bool cropped, int[]? inventoryBox = null) =>
        "Select only the visible pixels of this target as one editable layer: " + Quoted(name) +
        ". Trace every visible part, including disconnected pieces. For a whole person include hair, worn clothes, " +
        "both visible hands and footwear, excluding separately held objects. For a whole animal include its visible ears, " +
        "paws and tail. If the named target is itself a part or a prop, trace just that target. " +
        "Return outer and interior hole polygons. Follow the visible edge and put vertices at real turns and fine details. " +
        "List each perimeter once in order, omitting redundant points and the repeated closing point. " +
        "Each point is [x,y], normalized separately from 0 at top/left to 1000 at bottom/right. " +
        ShapeContext(cropped, inventoryBox) +
        "Follow the visible boundary. Exclude background apertures and occluding objects. " +
        "Set open=true only when the closing span of an outer contour is missing and needs to be inferred. " +
        "Return JSON with components. Use an empty list if the target is not visible.";

    private static string ShapeContext(bool cropped, int[]? box)
    {
        if (!cropped)
        {
            const string frame = "Use the context of the entire shown frame. ";
            return box is null ? frame : frame +
                $"The inventory roughly located the target in [{string.Join(",", box)}] using that same coordinate system. " +
                "Use that location to verify that the contour covers the entire visible target. ";
        }

        const string crop = "Work only in this shown crop. ";
        if (box is null) return crop;
        var width = box[2] - box[0];
        var height = box[3] - box[1];
        return Math.Max(width, height) >= 4 * Math.Min(width, height) ? crop +
            "Follow the full length of the target from end to end, including any slender sections. " +
            "Trace separate visible pieces on either side of an occlusion. " : crop;
    }

    private static readonly object Coordinate = new { type = "integer", minimum = 0, maximum = 1000 };
    private static readonly object Box = List(Coordinate, 4, 4);
    private static readonly object Polygon = new { type = "array", items = List(Coordinate, 2, 2), minItems = 3 };
    private static readonly object StringType = new { type = "string" };

    private static object Object(Dictionary<string, object> properties) =>
        new { type = "object", properties, required = properties.Keys.ToArray(), additionalProperties = false };

    private static object List(object items, int minimum, int maximum) =>
        new { type = "array", items, minItems = minimum, maxItems = maximum };

    internal static object MapSchema(LayerDepth depth, bool portraitParts = false)
    {
        var piece = new Dictionary<string, object>
        {
            ["id"] = StringType, ["parent"] = new { type = new[] { "string", "null" } },
            ["name"] = StringType, ["box"] = Box, ["fragment"] = new { type = "boolean" }
        };
        if (portraitParts) piece["person"] = new { type = "boolean" };
        return Object(new() { ["pieces"] = List(Object(piece), 0, Limit(depth)) });
    }

    internal static readonly object LocateSchema = Object(new() { ["box"] = Box });
    internal static readonly object LocatePersonSchema = Object(new() { ["box"] = Box, ["person"] = new { type = "boolean" } });
    internal static readonly object ShapeSchema = Object(new()
    {
        ["components"] = List(Object(new()
        {
            ["outer"] = Polygon, ["holes"] = List(Polygon, 0, 12), ["open"] = new { type = "boolean" }
        }), 0, 16)
    });

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions PromptJson = new() { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };

    private static string Quoted(string name) => JsonSerializer.Serialize(name, PromptJson);

    internal static LayerMap[] ReadMap(string answer, LayerDepth depth)
    {
        using var json = Read(answer);
        var all = json.RootElement.GetProperty("pieces").Deserialize<LayerMap[]>(Json) ?? [];
        if (all.Length > Limit(depth)) throw new InvalidDataException("ImageLayers.InvalidReply");
        var entries = all.Where(entry => entry is not null && entry.Id is { Length: > 0 and <= 80 }
            && entry.Id != "$target" && entry.Name is { Length: > 0 and <= 160 } && ValidBox(entry.Box)
            && !string.IsNullOrWhiteSpace(entry.Name) && !entry.Name.Any(char.IsControl)).ToList();
        var known = entries.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < entries.Count; i++)
            if (!seen.Add(entries[i].Id))
            {
                var suffix = entries[i].Id + "_" + i;
                while (!known.Add(suffix)) suffix += "_";
                entries[i] = entries[i] with { Id = suffix };
            }

        var byId = entries.ToDictionary(entry => entry.Id);
        for (var i = 0; i < entries.Count; i++)
        {
            var chain = new HashSet<string>(StringComparer.Ordinal) { entries[i].Id };
            for (var parent = entries[i].Parent; !string.IsNullOrEmpty(parent); parent = byId[parent].Parent)
                if (!chain.Add(parent) || !byId.ContainsKey(parent))
                {
                    entries[i] = entries[i] with { Parent = null };
                    break;
                }
            if (string.IsNullOrWhiteSpace(entries[i].Parent))
                entries[i] = entries[i] with { Parent = null, Fragment = false };
        }
        return entries.ToArray();
    }

    internal static int[]? ReadBox(string answer)
    {
        using var json = Read(answer);
        var box = json.RootElement.GetProperty("box").Deserialize<int[]>();
        return ValidBox(box) ? box : null;
    }

    internal static (int[]? Box, bool Person) ReadPersonBox(string answer)
    {
        using var json = Read(answer);
        var box = json.RootElement.GetProperty("box").Deserialize<int[]>();
        return ValidBox(box) ? (box, json.RootElement.GetProperty("person").ValueKind == JsonValueKind.True) : (null, false);
    }

    internal static LayerShape[] ReadShapes(string answer)
    {
        using var json = Read(answer);
        var shapes = json.RootElement.GetProperty("components").Deserialize<LayerShape[]>(Json) ?? [];
        if (shapes.Length > 16 || shapes.Any(shape => shape is null || !ValidPolygon(shape.Outer)
                || shape.Holes is null or { Length: > 12 } || shape.Holes.Any(hole => !ValidPolygon(hole))))
            throw new InvalidDataException("ImageLayers.InvalidReply");
        return shapes;
    }

    private static JsonDocument Read(string answer)
    {
        if (string.IsNullOrWhiteSpace(answer) || answer.Length > 512 * 1024)
            throw new InvalidDataException("ImageLayers.InvalidReply");
        return JsonDocument.Parse(answer, new() { MaxDepth = 16 });
    }

    private static bool ValidPolygon(int[][]? points) => points is { Length: >= 3 }
        && points.All(point => point is { Length: 2 } && point.All(coordinate => coordinate is >= 0 and <= 1000))
        && points.DistinctBy(point => (point[0], point[1])).Take(3).Count() == 3;

    internal static bool ValidBox(int[]? box) => box is { Length: 4 }
        && box.All(coordinate => coordinate is >= 0 and <= 1000) && box[0] < box[2] && box[1] < box[3];

    internal static bool NeedsSceneContext(int[] box)
    {
        var width = box[2] - box[0];
        var height = box[3] - box[1];
        return width * height >= 180_000 || Math.Max(width, height) >= 700 && Math.Min(width, height) >= 180;
    }

    internal static Rectangle PaddedBox(Rectangle image, int[] box)
        => ImageBox(image, box, padding: .08);

    internal static LayerRasterPiece RepairInvertedCrop(LayerRasterPiece piece, Rectangle image, int[] inventoryBox)
    {
        if (piece.Frame == image || piece.Components is not [{ Open: false, Outer: { Length: 4 } outer, Holes: [var hole] }]
            || outer.Any(point => point[0] is not (0 or 1000) || point[1] is not (0 or 1000))
            || outer.DistinctBy(point => (point[0], point[1])).Count() != 4)
            return piece;

        var expected = ImageBox(image, inventoryBox, padding: 0);
        var crop = piece.Frame;
        if (expected.Left <= crop.Left || expected.Top <= crop.Top || expected.Right >= crop.Right || expected.Bottom >= crop.Bottom)
            return piece;
        var enclosed = ImageBox(crop, [hole.Min(p => p[0]), hole.Min(p => p[1]),
            hole.Max(p => p[0]), hole.Max(p => p[1])], padding: 0);
        // The target was placed strictly inside a padded crop. Only replace an exact frame outline
        // when its single hole has much stronger support from the independently located target.
        var agreement = Overlap(expected, enclosed);
        return agreement >= .8 && agreement >= Overlap(expected, crop) + .1
            ? piece with { Components = [new(hole, [])] }
            : piece;
    }

    private static double Overlap(Rectangle first, Rectangle second)
    {
        var intersection = Rectangle.Intersect(first, second);
        var common = intersection.Width * intersection.Height;
        return common / (double)(first.Width * first.Height + second.Width * second.Height - common);
    }

    private static Rectangle ImageBox(Rectangle image, int[] box, double padding)
    {
        var x = (box[2] - box[0]) * padding;
        var y = (box[3] - box[1]) * padding;
        var bounds = Rectangle.FromLTRB(image.X + (int)Math.Floor((box[0] - x) * image.Width / 1000),
            image.Y + (int)Math.Floor((box[1] - y) * image.Height / 1000),
            image.X + (int)Math.Ceiling((box[2] + x) * image.Width / 1000),
            image.Y + (int)Math.Ceiling((box[3] + y) * image.Height / 1000));
        return Rectangle.Intersect(bounds, image);
    }
}
