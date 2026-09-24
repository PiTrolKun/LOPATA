using System.Text.Json;

namespace AIHub.Services.LiteraryImport;

public sealed class ImportRouteRow
{
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Existing { get; set; }
    public string SourceId { get; set; } = "";
    public string SourceRevision { get; set; } = "";
    public bool HasSource => !string.IsNullOrEmpty(SourceId);
}

public sealed class ImportRouteDraft
{
    public List<ImportRouteRow> Rows { get; set; } = [];
    public string Notes { get; set; } = "";
    public bool Scanned { get; set; }
    public static ImportRouteDraft Parse(string json, string previous)
    {
        if (json.Length == 0) return new() { Notes = previous };
        var value = JsonSerializer.Deserialize<ImportRouteDraft>(json);
        if (value?.Rows is null || value.Notes is null || value.Rows.Any(r => r is null || r.Title is null || r.Description is null))
            throw new System.IO.InvalidDataException("Literary.Import.PostQuestions.InvalidState");
        return value;
    }
    public string Serialize() => JsonSerializer.Serialize(this);
    public string Answer(bool english) => string.Join("\n\n", Rows.Where(r => r.Title.Trim().Length > 0 || r.Description.Trim().Length > 0)
        .Select(r => $"{r.Number}. {r.Title.Trim()} [{(r.Existing ? (english ? "written" : "написано") : (english ? "planned" : "план"))}]\n{r.Description.Trim()}"))
        + (Notes.Trim().Length > 0 ? "\n\n" + Notes.Trim() : "");
}
