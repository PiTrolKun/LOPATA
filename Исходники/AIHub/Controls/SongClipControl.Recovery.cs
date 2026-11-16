using System.IO;

namespace AIHub.Controls;

public sealed partial class SongClipControl
{
    internal string RecoveryProjectId => _project.Id;

    internal void RestoreRecoveryProject(string? project)
    {
        if (Guid.TryParseExact(project, "N", out _) && _project.Id != project
            && File.Exists(Path.Combine(_store.Folder(project!), "project.json")))
            OpenProject(project!);
    }
}
