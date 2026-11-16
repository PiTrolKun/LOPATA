namespace AIHub.Controls;

public sealed partial class MusicPreparationControl
{
    internal string? RecoveryProjectId =>
        IsWorkspace && _workspace?.Projects.Current is { Persistent: true } project ? project.Id : null;

    internal void RestoreRecoveryProject(string? project)
    {
        if (IsWorkspace || IsBusy || !Guid.TryParseExact(project, "N", out _)) return;
        EnsureWorkspace();
        _workspace!.Projects.Open(project!);
        _variation = _workspace.Generation.Variation;
        _preparation.Variation = _variation;
        ShowWorkspace();
    }
}
