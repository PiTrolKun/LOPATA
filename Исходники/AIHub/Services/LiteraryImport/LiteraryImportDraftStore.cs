using System.IO;
using System.Text.Json;

namespace AIHub.Services.LiteraryImport;

public sealed record LiteraryImportDraft(string Id, string Folder, string SourcePath,
    string ProjectName, string WorkTitle, string SessionRoot, string Step,
    string[] DialogIds, DateTimeOffset LastAccessed)
{
    public string[] SelectedUnitIds { get; init; } = [];
    public string[] SplitGroupKeys { get; init; } = [];
    public string[] ConfirmedGroupKeys { get; init; } = [];
    public Dictionary<string, string> EditedGroupNames { get; init; } = new(StringComparer.Ordinal);
    public string WorkSelectionKey { get; init; } = "";
    public string[] SelectedWorkUnitIds { get; init; } = [];
}

/// <summary>Small local index of unfinished imports. Removing a shortcut never deletes a session.</summary>
public sealed class LiteraryImportDraftStore(string path)
{
    public const int Limit = 3;
    public static LiteraryImportDraftStore Default() => new(Path.Combine(AppDataPaths.BaseDirectory, "Literary", "import-drafts.json"));

    public IReadOnlyList<LiteraryImportDraft> Load() => Read()
        .Where(e => !IsFinished(e)).OrderByDescending(e => e.LastAccessed).Take(Limit).ToArray();

    public void Remember(LiteraryImportDraft draft)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var indexLock = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var entries = Read().Where(e => e.Id != draft.Id && !IsFinished(e)).ToList();
        if (!IsFinished(draft)) entries.Add(draft with { LastAccessed = DateTimeOffset.Now });
        LiteraryChapterFiles.Write(path, JsonSerializer.Serialize(entries
            .OrderByDescending(e => e.LastAccessed).Take(Limit), ImportJson.Options));
    }

    public void Forget(string id)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var indexLock = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        LiteraryChapterFiles.Write(path, JsonSerializer.Serialize(Read().Where(e => e.Id != id), ImportJson.Options));
    }

    private LiteraryImportDraft[] Read()
    {
        if (!File.Exists(path)) return [];
        LiteraryImportDraft[] ReadFile(string file)
        {
            var entries = JsonSerializer.Deserialize<LiteraryImportDraft[]>(LiteraryChapterFiles.Read(file), ImportJson.Options);
            if (entries is null || entries.Any(e => e is null || string.IsNullOrWhiteSpace(e.Id)
                || e.Folder is null || e.SourcePath is null || e.ProjectName is null || e.WorkTitle is null
                || e.SessionRoot is null || e.Step is null || e.DialogIds is null
                || e.SelectedUnitIds is null || e.SplitGroupKeys is null || e.ConfirmedGroupKeys is null
                || e.EditedGroupNames is null || e.WorkSelectionKey is null || e.SelectedWorkUnitIds is null)
                || entries.Select(e => e.Id).Distinct().Count() != entries.Length)
                throw new InvalidDataException("Invalid import draft list.");
            return entries;
        }
        try { return ReadFile(path); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        { if (File.Exists(path + ".bak")) return ReadFile(path + ".bak"); throw; }
    }

    private static bool IsFinished(LiteraryImportDraft entry)
    {
        if (entry.SessionRoot.Length == 0) return false;
        try
        {
            var file = Path.Combine(entry.SessionRoot, "session.json");
            if (!File.Exists(file)) return false;
            var state = JsonSerializer.Deserialize<ImportSessionState>(File.ReadAllText(file), ImportJson.Options);
            return state?.Stage is "complete" or "partial-result";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }
}
