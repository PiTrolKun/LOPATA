using System.IO;
using System.Text.Json;

namespace AIHub.Services;

public sealed record LiteraryInterviewRecent(string Id, string Title, string Root, int Step, DateTimeOffset UpdatedAt);

/// <summary>Only local shortcuts are retained here. Eviction never deletes project files.</summary>
public sealed class LiteraryInterviewRecentStore(string path)
{
    public static LiteraryInterviewRecentStore Default() => new(Path.Combine(AppDataPaths.BaseDirectory, "Literary", "preparations.json"));
    public const int Limit = 3;

    public IReadOnlyList<LiteraryInterviewRecent> Load() => Read().Where(IsUnfinished)
        .OrderByDescending(e => e.UpdatedAt).Take(Limit).ToArray();

    public void Remember(string root, Models.LiteraryInterviewState state)
    {
        root = Path.GetFullPath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var indexLock = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var entries = Read().Where(e => !string.Equals(e.Root, root, StringComparison.OrdinalIgnoreCase) && IsUnfinished(e)).ToList();
        if (!state.Finished && !File.Exists(Path.Combine(root, "project.json")))
            entries.Add(new(state.Id, state.ProjectName, root, state.Step, state.SavedAt ?? DateTimeOffset.UtcNow));
        LiteraryChapterFiles.Write(path, JsonSerializer.Serialize(entries.OrderByDescending(e => e.UpdatedAt).Take(Limit), LiteraryInterviewSession.Json));
    }

    private LiteraryInterviewRecent[] Read()
    {
        if (!File.Exists(path)) return [];
        LiteraryInterviewRecent[] ReadFile(string file)
        {
            var entries = JsonSerializer.Deserialize<LiteraryInterviewRecent[]>(LiteraryChapterFiles.Read(file));
            if (entries is null || entries.Any(e => e is null || string.IsNullOrWhiteSpace(e.Id)
                || !Path.IsPathFullyQualified(e.Root) || e.Step is < 1 or > 37)
                || entries.Select(e => e.Root).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Length)
                throw new InvalidDataException("Invalid preparation list.");
            return entries;
        }
        try { return ReadFile(path); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        { if (File.Exists(path + ".bak")) return ReadFile(path + ".bak"); throw; }
    }

    private static bool IsUnfinished(LiteraryInterviewRecent entry)
    {
        if (File.Exists(Path.Combine(entry.Root, "project.json"))) return false;
        // Keep inaccessible or moved folders visible so the user can locate/import them.
        try { return !LiteraryInterviewSession.Read(entry.Root).Finished; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return true; }
    }
}
