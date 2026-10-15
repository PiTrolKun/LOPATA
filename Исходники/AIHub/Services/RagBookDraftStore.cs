using System.IO;
using System.Text.Json;

namespace AIHub.Services;

public sealed record RagBookDraft
{
    public string Name { get; init; } = "";
    public string OutputDirectory { get; init; } = "";
    public string[] Books { get; init; } = [];
    public int CpuTargetPercent { get; init; } = 50;
}

/// <summary>Workspace selections only; this is not a processing checkpoint or a ready library.</summary>
public sealed class RagBookDraftStore(string path)
{
    public RagBookDraft Load()
    {
        if (!File.Exists(path)) return new();
        try
        {
            var draft = JsonSerializer.Deserialize<RagBookDraft>(File.ReadAllText(path));
            if (draft is null || draft.Name is null || draft.OutputDirectory is null || draft.Books is null)
                throw new JsonException("Incomplete draft.");
            return draft;
        }
        catch (JsonException error) { throw new IOException("The saved book workspace cannot be read; the original file was retained.", error); }
    }

    public void Save(RagBookDraft draft)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, draft, new JsonSerializerOptions { WriteIndented = true });
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
