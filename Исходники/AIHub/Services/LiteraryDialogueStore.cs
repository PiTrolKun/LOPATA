using System.IO;
using System.Text.Json;

namespace AIHub.Services;

public sealed record LiteraryDialogueMessage(bool User, string Text, bool Complete = true);
public sealed class LiteraryDialogue
{
    public int Version { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Role { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string Input { get; set; } = "";
    public List<LiteraryDialogueMessage> Messages { get; set; } = [];
    public string Partial { get; set; } = "";
    public bool Generating { get; set; }
}

public sealed class LiteraryDialogueStore(LiteraryProjectLayout layout, LiteraryChatProfile role)
{
    private string? _lastSaved;
    public string FilePath => Path.Combine(layout.Dialogs, role.ToString(), "dialog.json");
    public LiteraryDialogue Load()
    {
        layout.EnsurePresent();
        if (!File.Exists(FilePath)) return new() { ProjectId = layout.ProjectId, Role = role.ToString() };
        var data = JsonSerializer.Deserialize<LiteraryDialogue>(LiteraryChapterFiles.Read(FilePath))
            ?? throw new InvalidDataException("Empty dialogue.");
        if (data.Version != 1 || data.ProjectId != layout.ProjectId || data.Role != role.ToString() || data.Messages is null)
            throw new InvalidDataException("Dialogue identity mismatch.");
        if (data.Generating)
        {
            if (data.Partial.Length > 0) data.Messages.Add(new(false, data.Partial, false));
            data.Partial = ""; data.Generating = false;
        }
        return data;
    }
    public void Save(LiteraryDialogue data)
    {
        if (data.ProjectId != layout.ProjectId || data.Role != role.ToString()) throw new InvalidDataException("Dialogue identity mismatch.");
        layout.EnsurePresent();
        var json = JsonSerializer.Serialize(data);
        if (json == _lastSaved) return;
        layout.EnsureFolder(Path.Combine("Dialogs", role.ToString()));
        LiteraryChapterFiles.Write(FilePath, json); _lastSaved = json;
    }
}
