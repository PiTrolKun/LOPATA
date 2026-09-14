using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIHub.Services;

// Edit only the creator's final brief; preserve the original interview and unrelated project fields.
public sealed class LiteraryCalibrationStore(string root)
{
    private readonly string _path = Path.Combine(root, "project.json");
    private string? _snapshot;
    public bool HasOpened => File.Exists(Path.Combine(root,"Calibration","opened.json"));
    public void MarkOpened()
    {
        var layout=new LiteraryProjectLayout(root);
        LiteraryChapterFiles.Write(Path.Combine(layout.EnsureFolder("Calibration"),"opened.json"),"{\"version\":1}");
    }
    public string Read()
    {
        _snapshot = LiteraryChapterFiles.Read(_path);
        return JsonNode.Parse(_snapshot)?["CreationBrief"]?.GetValue<string>() ?? "";
    }
    public void Save(string text)
    {
        using var gate = new FileStream(_path + ".calibration.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var current = LiteraryChapterFiles.Read(_path);
        if (_snapshot is null || current != _snapshot) throw new IOException("Project changed while the editor was open.");
        var document = JsonNode.Parse(current)?.AsObject() ?? throw new InvalidDataException("Invalid project.");
        document["CreationBrief"] = text;
        var updated = document.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        LiteraryChapterFiles.Write(_path, updated);
        _snapshot = updated;
    }
}
