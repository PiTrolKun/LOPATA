using System.IO;

namespace AIHub.Services;

/// <summary>Preparation already belongs to its final parent project, even before confirmation.</summary>
public sealed class LiteraryProjectReservation : IDisposable
{
    public string Root { get; }
    private readonly FileStream _lease;
    public LiteraryProjectReservation(string parent, string name)
    {
        if (!Directory.Exists(parent) || !Path.IsPathFullyQualified(parent) || !LiteraryProjectStore.IsValidProjectName(name))
            throw new IOException("Choose the storage folder and project name before adding materials.");
        Root = Path.Combine(Path.GetFullPath(parent), name);
        LiteraryProjectLayout.CheckTreePath(Root);
        var marker = Path.Combine(Root, ".creation");
        if (Directory.Exists(Root) && !File.Exists(marker))
            throw new IOException("Project directory already exists.");
        Directory.CreateDirectory(Root);
        _lease = new FileStream(Path.Combine(Root, ".creation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (!File.Exists(marker)) LiteraryChapterFiles.Write(marker, "LOPATA literary preparation v2");
        else if (LiteraryChapterFiles.Read(marker) != "LOPATA literary preparation v2")
        { _lease.Dispose(); throw new IOException("Unknown project preparation directory."); }
    }
    public void Dispose() => _lease.Dispose();
}
