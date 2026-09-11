using System.IO;
using System.Text.Json;

namespace AIHub.Services;

/// <summary>Project-owned data only; shared weights and application settings live elsewhere.</summary>
public sealed class LiteraryProjectLayout
{
    public string Root { get; }
    public string ProjectId { get; }
    public string Rag => Path.Combine(Root, "Rag");
    public string Database => Path.Combine(Rag, "Qdrant");
    public string Dialogs => Path.Combine(Root, "Dialogs");
    public string Logs => Path.Combine(Root, "Diagnostics");
    public string Exports => Path.Combine(Root, "Exports");
    public string Marker => Path.Combine(Root, "storage.json");
    public LiteraryProjectLayout(string root)
    {
        Root = Path.GetFullPath(root);
        ProjectId = LiteraryProjectStore.ReadProject(Root).Id;
        EnsurePresent();
    }
    public void EnsurePresent()
    {
        if (!Directory.Exists(Root) || LiteraryProjectStore.ReadProject(Root).Id != ProjectId)
            throw new IOException("The project is unavailable or its identity changed.");
        CheckTreePath(Root);
    }
    public string EnsureFolder(string relative)
    {
        EnsurePresent();
        var path = Path.GetFullPath(Path.Combine(Root, relative));
        if (!path.StartsWith(Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Path is outside the project.");
        CheckTreePath(path); Directory.CreateDirectory(path); return path;
    }
    public static void CheckTreePath(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Project storage does not follow directory links.");
    }
    public void Initialize()
    {
        foreach (var folder in new[] { "chapters", "Materials", "Rag/Source", "Rag/Work", "Rag/Staging", "Dialogs", "Exports", "Jelly", "Diagnostics" })
            EnsureFolder(folder);
    }
    public QdrantRuntime CreateRuntime() => new(new QdrantOptions { DataDirectory = Database, ValidateStorage = EnsurePresent });
    public bool Migrated
    {
        get
        {
            if (!File.Exists(Marker)) return false;
            using var data = JsonDocument.Parse(LiteraryChapterFiles.Read(Marker));
            if (data.RootElement.GetProperty("version").GetInt32() != 2 || data.RootElement.GetProperty("projectId").GetString() != ProjectId)
                throw new InvalidDataException("Unsupported or foreign project storage marker.");
            return true;
        }
    }
    public void CommitLayout()
    {
        EnsurePresent();
        LiteraryChapterFiles.Write(Marker, JsonSerializer.Serialize(new { version = 2, projectId = ProjectId,
            chapters = "chapters", sources = "Materials", rag = "Rag", dialogs = "Dialogs", exports = "Exports", jelly = "Jelly" }));
    }
}
