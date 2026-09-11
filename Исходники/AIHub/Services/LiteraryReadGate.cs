using System.Text.Json;

namespace AIHub.Services;

/// <summary>Evidence means retained source text, not model confidence or catalog presence.</summary>
public sealed class LiteraryReadGate(LiteraryReadRoute route)
{
    public const int MaxAttempts = 3;
    private readonly Dictionary<string, int> _attempts = new() { ["project"] = 0, ["reference"] = 0 };
    public IEnumerable<string> Required => new[] { route.Project ? "project" : "", route.Reference ? "reference" : "" }.Where(s => s.Length > 0);
    public Queue<LiteraryReadAction> Initial => new(Required.Select(DefaultAction));
    public LiteraryReadAction DefaultAction(string corpus) => corpus == "reference"
        ? new("semantic_reference", Query: route.ReferenceQuery)
        : route.PartNumber.Length > 0 ? new("read", route.PartNumber) : new("semantic_project", Query: route.ProjectQuery);
    public void Attempt(LiteraryReadAction action)
    {
        var corpus = action.Action.Contains("reference") ? "reference" : "project";
        if (action.Action is not ("answer" or "list" or "list_reference")) _attempts[corpus]++;
    }
    public string? Pending(IEnumerable<LiteraryReadResult> materials) => Required.FirstOrDefault(c => !Has(materials, c) && _attempts[c] < MaxAttempts);
    public string[] Missing(IEnumerable<LiteraryReadResult> materials) => Required.Where(c => !Has(materials, c)).ToArray();
    public object Status(IEnumerable<LiteraryReadResult> materials) => new
    {
        scope = route.Scope, attempts = _attempts, missing = Missing(materials),
        note = "Retained excerpts only; presence does not prove relevance or truth of the final answer. Missing evidence is not absence of a fact in the source."
    };
    public static string[] Actions(string? pending) => pending switch
    {
        "reference" => ["semantic_reference", "search_reference", "read_reference"],
        "project" => ["semantic_project", "search", "read"],
        _ => ["answer", "read", "list", "search", "list_reference", "read_reference", "search_reference", "semantic_reference", "semantic_project"]
    };
    public static bool Has(IEnumerable<LiteraryReadResult> materials, string corpus) => materials.Any(m => Contains(m.Json, corpus));
    private static bool Contains(string json, string corpus)
    {
        using var document = JsonDocument.Parse(json);
        bool Visit(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().Any(Visit);
            if (value.ValueKind != JsonValueKind.Object) return false;
            var kind = value.TryGetProperty("kind", out var k) ? k.GetString() : null;
            var history = value.TryGetProperty("state", out var s) && s.GetString() == "history";
            if (value.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(text.GetString())
                && (corpus == "reference" ? kind == "reference" : kind == "project_history" || kind == "fragment" && history)) return true;
            return value.EnumerateObject().Any(p => Visit(p.Value));
        }
        return Visit(document.RootElement);
    }
}
