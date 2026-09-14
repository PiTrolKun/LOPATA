using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIHub.Services;

public sealed record CalibrationField(int Topic, string Label, string Value, Action<string> Update, int? Step = null, bool? Adaptive = null);

// Present the supported brief as fields while retaining all original metadata and ordering.
public sealed class LiteraryCalibrationDocument
{
    private readonly string _original;
    private JsonObject? _root;
    private string _plain;
    private bool _changed;
    public List<CalibrationField> Fields { get; } = [];
    public bool Structured => _root is not null;
    public LiteraryCalibrationDocument(string text)
    {
        _original = _plain = text;
        try
        {
            var root = JsonNode.Parse(text) as JsonObject;
            if (root?["sections"] is not JsonArray sections) return;
            var fields = new List<CalibrationField>();
            void Add(JsonObject item, string key, int topic, string label)
            {
                var value = item[key]?.GetValue<string>() ?? "";
                fields.Add(new(topic, label, value, updated => { if (updated != (item[key]?.GetValue<string>() ?? "")) { item[key] = updated; _changed = true; } }, item["Step"]?.GetValue<int>(), item["Adaptive"]?.GetValue<bool>()));
            }
            foreach (var node in sections)
            {
                var item = node?.AsObject() ?? throw new JsonException();
                Add(item, "Text", item["Topic"]?.GetValue<int>() ?? -1, item["Question"]?.GetValue<string>() ?? "");
            }
            if (root["route"] is JsonArray route)
                foreach (var node in route)
                {
                    var item = node?.AsObject() ?? throw new JsonException();
                    var number = item["Number"]?.GetValue<int>() ?? 0;
                    Add(item, "Title", 8, $"route-title:{number}");
                    Add(item, "Description", 8, $"route-description:{number}");
                }
            else if (root["route"] is not null) return;
            _root = root;
            Fields.AddRange(fields);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        { _root = null; Fields.Clear(); }
    }
    public void SetPlain(string text) { _plain = text; }
    public string Serialize() => _root is null ? _plain : !_changed ? _original : _root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}
