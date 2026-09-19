using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIHub.Services;

public sealed class LiteraryRouteStep(int number, string title, string description)
{
    public int Number { get; } = number;
    public string Title { get; set; } = title;
    public string Description { get; set; } = description;
}

// Route numbers are persistent source IDs. Editing or appending must never renumber them.
public sealed class LiteraryRouteDocument
{
    private readonly string _original;
    private readonly JsonObject _root;
    private readonly Dictionary<int, JsonObject> _originalSteps = [];
    public List<LiteraryRouteStep> Steps { get; } = [];
    public bool WasPlain { get; }

    public LiteraryRouteDocument(string text)
    {
        _original = text;
        var document = new LiteraryCalibrationDocument(text);
        WasPlain = !document.Structured;
        _root = document.Structured ? JsonNode.Parse(text)!.AsObject() : new JsonObject
        {
            ["kind"] = "confirmed_creation_intent_not_manuscript_events",
            ["sections"] = text.Length == 0 ? new JsonArray() : new JsonArray(new JsonObject
            { ["Topic"] = 0, ["Question"] = "", ["Text"] = text }),
            ["route"] = new JsonArray()
        };
        try
        {
            if (_root["route"] is not JsonArray route) return;
            foreach (var node in route)
            {
                var item = node?.AsObject() ?? throw new JsonException();
                var number = item["Number"]?.GetValue<int>() ?? 0;
                if (number <= 0 || !_originalSteps.TryAdd(number, item)) throw new JsonException();
                Steps.Add(new(number, item["Title"]?.GetValue<string>() ?? "", item["Description"]?.GetValue<string>() ?? ""));
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        { throw new InvalidOperationException("Studio.Route.Invalid", ex); }
    }

    public LiteraryRouteStep Add()
    {
        var step = new LiteraryRouteStep(checked(Steps.Select(s => s.Number).DefaultIfEmpty(0).Max() + 1), "", "");
        Steps.Add(step); return step;
    }

    public bool Changed => Steps.Count != _originalSteps.Count || Steps.Any(s =>
        !_originalSteps.TryGetValue(s.Number, out var original) || s.Title != (original["Title"]?.GetValue<string>() ?? "")
        || s.Description != (original["Description"]?.GetValue<string>() ?? ""));

    public string Serialize()
    {
        if (!Changed) return _original;
        if (Steps.Any(s => s.Number <= 0) || Steps.Select(s => s.Number).Distinct().Count() != Steps.Count
            || !_originalSteps.Keys.All(n => Steps.Any(s => s.Number == n)))
            throw new InvalidOperationException("Studio.Route.Invalid");
        if (Steps.Any(s => string.IsNullOrWhiteSpace(s.Title)))
            throw new InvalidOperationException("Studio.Route.TitleRequired");
        var root = _root.DeepClone().AsObject(); var route = new JsonArray();
        foreach (var step in Steps)
        {
            var item = _originalSteps.TryGetValue(step.Number, out var original) ? original.DeepClone().AsObject() : new JsonObject();
            item["Number"] = step.Number; item["Title"] = step.Title; item["Description"] = step.Description;
            route.Add(item);
        }
        root["route"] = route;
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }
}
