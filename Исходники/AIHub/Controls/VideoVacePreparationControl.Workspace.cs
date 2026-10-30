namespace AIHub.Controls;

internal sealed partial class VideoVacePreparationControl
{
    private bool _workspaceActive;
    private VideoVaceWorkspaceControl? _workspace;
    internal Func<VideoVaceWorkspaceControl>? CreateWorkspace { get; set; }
    private bool BuildWorkspace()
    {
        if (!_workspaceActive) return false;
        if (_workspace is null)
        {
            _workspace = CreateWorkspace?.Invoke() ?? throw new InvalidOperationException("VideoPrep.VaceRuntimeRequired");
            _workspace.BackRequested += RequestBack;
            _workspace.HomeRequested += RequestHome;
        }
        _workspace.Configure(_localize); Content = _workspace; return true;
    }
    private void OpenWorkspace()
    {
        if (!Ready || Busy || CreateWorkspace is null) return;
        _workspaceActive = true; Build();
    }
    internal void ShowSavedResults() { _workspaceActive = true; Build(); _workspace?.ReloadResults(); }
    internal void SetWorkspaceBusy(bool busy) => _workspace?.SetBusy(busy);
    internal void SetGenerating(bool active, bool pending = false) => _workspace?.SetGenerating(active, pending);
    internal void SetScheduled(DateTimeOffset? due) => _workspace?.SetScheduled(due);
    internal void NotifyWorkspace(string message) => _workspace?.Notify(message);
    internal void RefreshResults() => _workspace?.ReloadResults();
    internal void GenerationProgress(string message) => _workspace?.GenerationProgress(message);
}
