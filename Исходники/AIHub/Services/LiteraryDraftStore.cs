using System.IO;
using System.Text;

namespace AIHub.Services;

/// <summary>Working draft only. Does not approve a chapter or modify project metadata.</summary>
public sealed class LiteraryDraftStore(string projectDirectory)
{
    public string FilePath { get; } = Path.Combine(Path.GetFullPath(projectDirectory), "working-draft.txt");
    public string Load() => File.Exists(FilePath) ? File.ReadAllText(FilePath, new UTF8Encoding(false, true)) : "";
    public void Save(string text)
    {
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, text, new UTF8Encoding(false));
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
