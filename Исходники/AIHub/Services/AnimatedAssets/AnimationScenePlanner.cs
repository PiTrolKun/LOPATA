using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIHub.Models;
using AIHub.Services.VisualScenario;

namespace AIHub.Services.AnimatedAssets;

internal sealed record AnimationScenePlan(List<AnimationPose> Poses, string Keep, string Ambient, AnimationModelUse Model);

internal static class AnimationScenePlanner
{
    private const string Instructions = """
        You are LOPATA's animation Advisor. Plan one short loop from a reference image and the user's request.
        The application provides the actual original frame at both t=0 and the end. Return only distinct interior still
        poses. Omit a separate "return to the original pose" card. Give one to four cards. Give every visible action,
        later expression and delayed motion its own frozen pose.
        Do not invent motions.
        For a contact pose, describe exactly which limb touches which part of the object. Respect the supplied camera,
        framing, identity, props, style and any user-supplied locks. List only the visual details that should be fixed
        in anchors. Do not put time labels or actions in anchors.
        Each artist_prompt is a standalone English instruction to edit the same reference into that frozen pose.
        State what to change and what to keep. For requested moving elements such as curtains or clothes, depict their
        position at that instant. Put requested ambient motion such as mist in ambient, with occlusion and cyclic reset.
        Use null for ambient when there is none. Other unsupported additive effects belong in the appropriate still edit.
        Use on_pose objects of kind "lightning" for a flash or bolt and kind "notes" for moving musical notes.
        Write one object for each occurrence the user asked for, only on the matching card. When asked for flash and notes
        at a strike, put BOTH objects, one of each kind, in the on_pose array of the card where the limb touches the strings.
        Leave on_pose empty for the anticipation and later reaction if no effects are requested there. Do not repeat an
        effect on several poses. Give each effect a short location description and normalized x/y coordinates
        (0=left/top, 1=right/bottom), or null if a coordinate cannot be inferred. The program draws these overlays.
        The artist_prompt describes the frozen physical pose and preserves marks already visible in the reference.
        It does not add or move lightning or notes.
        Keep all timestamps increasing, on quarter seconds, strictly between zero and the end of the loop.
        """;

    internal static async Task<AnimationScenePlan> PlanAsync(VisualAdvisor advisor, VisualPreparedAdvisor prepared,
        string imageUrl, string description, double duration, string keep, string language,
        AnimationModelUse model, CancellationToken token)
    {
        var instruction = Instructions + "\nExact loop duration: "
            + duration.ToString("0.###", CultureInfo.InvariantCulture)
            + " seconds. Write the action, anchors, placement and ambient descriptions in " + language + ".";
        var message = string.IsNullOrWhiteSpace(keep) ? description : $"{description}\n\nPreserve exactly: {keep}";
        var raw = await advisor.CompleteAsync(prepared,
            [VisualAdvisor.Message("system", instruction), VisualAdvisor.ImageMessage(message, [imageUrl])],
            token, responseFormat: ResponseFormat(duration), maximumReplyTokens: 2400);
        return Parse(raw, duration, keep, model);
    }

    internal static object ResponseFormat(double duration)
    {
        var times = Enumerable.Range(1, (int)Math.Ceiling(duration * 4) - 1)
            .Select(index => index / 4d).ToArray();
        var text = new { type = "string", minLength = 1 };
        var coordinate = new { anyOf = new object[] { new { type = "number" }, new { type = "null" } } };
        var effect = Closed(new()
        {
            ["kind"] = new { @enum = new[] { "lightning", "notes" } },
            ["duration_s"] = new { type = "number" },
            ["placement"] = text,
            ["x"] = coordinate,
            ["y"] = coordinate
        });
        var card = Closed(new()
        {
            ["at_s"] = new { @enum = times },
            ["action"] = text,
            ["artist_prompt"] = text,
            ["on_pose"] = new { type = "array", items = effect, maxItems = 2 }
        });
        var schema = Closed(new()
        {
            ["anchors"] = new { type = "array", items = text, minItems = 1, maxItems = 8 },
            ["cards"] = new { type = "array", items = card, minItems = 1, maxItems = 4 },
            ["ambient"] = new { anyOf = new object[] { text, new { type = "null" } } }
        });
        return new { type = "json_schema", json_schema = new { name = "scene_cards", strict = true, schema } };
    }

    private static object Closed(Dictionary<string, object> properties) =>
        new { type = "object", properties, required = properties.Keys.ToArray(), additionalProperties = false };

    internal static AnimationScenePlan Parse(string raw, double duration, string keep, AnimationModelUse model)
    {
        try
        {
            using var document = JsonDocument.Parse(raw.Trim());
            var root = document.RootElement;
            var anchors = root.GetProperty("anchors").EnumerateArray().Select(item => item.GetString()?.Trim())
                .Where(IsVisualAnchor).ToArray();
            var ambient = root.GetProperty("ambient").GetString()?.Trim() ?? "";
            if (ambient.Equals("null", StringComparison.OrdinalIgnoreCase)) ambient = "";
            var remaining = root.GetProperty("cards");
            if (remaining.GetArrayLength() is < 1 or > 4) throw InvalidPlan();
            List<AnimationPose> poses = [];
            var previous = 0d;
            foreach (var item in remaining.EnumerateArray())
            {
                var time = item.GetProperty("at_s").GetDouble();
                if (!double.IsFinite(time) || time <= previous || time >= duration || time * 4 != Math.Round(time * 4))
                    throw InvalidPlan();
                var pose = new AnimationPose
                {
                    Time = time,
                    Text = ReadText(item.GetProperty("action")),
                    Prompt = ReadText(item.GetProperty("artist_prompt"))
                };
                var effects = item.GetProperty("on_pose");
                if (effects.GetArrayLength() > 2) throw InvalidPlan();
                foreach (var effect in effects.EnumerateArray())
                {
                    var kind = effect.GetProperty("kind").GetString();
                    var length = effect.GetProperty("duration_s").GetDouble();
                    if (kind is not ("lightning" or "notes") || !double.IsFinite(length) || length <= 0)
                        throw InvalidPlan();
                    pose.Effects.Add(new()
                    {
                        Kind = kind,
                        Text = ReadText(effect.GetProperty("placement")),
                        Duration = Math.Min(length, duration - time),
                        X = Coordinate(effect.GetProperty("x")),
                        Y = Coordinate(effect.GetProperty("y"))
                    });
                }
                poses.Add(pose);
                previous = time;
            }
            var constraints = string.Join("\n", new[] { keep }.Concat(anchors).Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase));
            return new(poses, constraints, ambient, model);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw InvalidPlan();
        }
    }

    private static string ReadText(JsonElement value) => value.GetString()?.Trim() is { Length: > 0 } text
        ? text : throw InvalidPlan();

    private static double? Coordinate(JsonElement point) => point.ValueKind == JsonValueKind.Number
        && point.TryGetDouble(out var number) && double.IsFinite(number) && number is >= 0 and <= 1 ? number : null;

    private static bool IsVisualAnchor(string? value) => !string.IsNullOrWhiteSpace(value) && value.Any(char.IsLetter)
        && !Regex.IsMatch(value, @"^(?:t\s*=\s*)?\d+(?:[.,]\d+)?\s*(?:s(?:ec)?|с(?:ек(?:унда|унды|унд)?)?)(?:$|[_\s\-])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static BackgroundOperationFailedException InvalidPlan() => new("Animation.InvalidScenePlan");
}
