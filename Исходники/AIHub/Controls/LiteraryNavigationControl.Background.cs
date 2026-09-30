using System.IO;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Controls;

public sealed partial class LiteraryNavigationControl
{
    public string? CurrentWorkspaceDirectory => _workspace?.ProjectDirectory;

    public bool CheckpointBackgroundState()
        => (_workspace?.CheckpointBackgroundState() ?? true)
            & (_interview?.CheckpointBackgroundState() ?? true)
            & (_import?.CheckpointBackgroundState() ?? true);

    public void RestoreWorkspace(string directory)
    {
        var path = Path.GetFullPath(directory);
        if (_workspace?.ProjectDirectory.Equals(path, StringComparison.OrdinalIgnoreCase) == true) return;
        if (_workspace is not null && !_workspace.CanLeave()) throw new BackgroundOperationWaitingException("workspace_busy");
        var entry = _store.Load().Projects.SingleOrDefault(p => Path.GetFullPath(p.ProjectPath).Equals(path, StringComparison.OrdinalIgnoreCase))
            ?? throw new BackgroundOperationWaitingException("project_missing_from_index");
        var project = LiteraryProjectStore.ReadProject(path);
        _workspace = new LiteraryWorkspaceControl(entry, project, _l, restoringBackground: true);
        _workspace.BackRequested += GoBack;
        _workspace.HomeRequested += () => { _workspace = null; SetValue(WorkspaceStatusProperty, null); HomeRequested?.Invoke(); };
        ShowingProjects = true; Render();
    }

    public async Task ResumeBackgroundStudioAsync(BackgroundOperationState operation, CancellationToken token)
    {
        if (operation.Project is null) throw new InvalidDataException("Missing project path.");
        RestoreWorkspace(operation.Project);
        await _workspace!.ResumeBackgroundStudioAsync(operation, token);
    }

    internal async Task ResumeBackgroundMemoryAsync(BackgroundOperationState operation, CancellationToken token)
    {
        if (operation.Project is null) throw new InvalidDataException("Missing project path.");
        RestoreWorkspace(operation.Project);
        await _workspace!.ResumeBackgroundMemoryAsync(operation, token);
    }

    public bool ViewBackgroundResult(BackgroundOperationNotice notice)
    {
        if (notice.Project is null) return false;
        RestoreWorkspace(notice.Project);
        return notice.Kind == LiteraryWorkspaceControl.MemoryBackgroundKind || _workspace!.ViewBackgroundResult(notice);
    }
}

public sealed partial class LiteraryWorkspaceControl
{
    public string ProjectDirectory => _entry.ProjectPath;
    internal bool CheckpointBackgroundState()
        => _draft.Save() & (_studio?.Save() ?? true) & _writer.SaveDialogue() & _advisor.SaveDialogue();
    internal Task ResumeBackgroundStudioAsync(BackgroundOperationState operation, CancellationToken token)
        => _studio?.ResumeBackgroundRequestAsync(operation, token) ?? throw new BackgroundOperationWaitingException("studio_unavailable");
    internal bool ViewBackgroundResult(BackgroundOperationNotice notice) => _studio?.ViewBackgroundResult(notice) == true;
}
