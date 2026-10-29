using AIHub.Services.VideoPreparation;

namespace AIHub.Controls;

public sealed partial class VideoPreparationControl
{
    private VideoH3PreparationControl? _generatorPage;
    public event Action? HomeRequested;
    internal VideoH3PreparationControl ShowH3Preparation(string root, int connections, Func<IVideoH3Preparation>? create = null)
    {
        var page = new VideoH3PreparationControl(_localize, root, connections, create);
        page.CreateWorkspace = () => CreateH3Workspace(page);
        page.HomeRequested += () =>
        {
            if (ReferenceEquals(page, _generatorPage) && Checkpoint()) HomeRequested?.Invoke();
        };
        page.BackRequested += () =>
        {
            if (!ReferenceEquals(page, _generatorPage)) return;
            _generatorPage = null; Render();
        };
        _generatorPage = page; Render(); return page;
    }
    private VideoH3WorkspaceControl CreateH3Workspace(VideoH3PreparationControl page)
    {
        var workspace = new VideoH3WorkspaceControl(_localize, Session.Project, () =>
        { Session.Changed(); _autosave.Stop(); _autosave.Start(); });
        workspace.SaveRequested += () => { if (Save()) workspace.Notify(T("H3SettingsSaved")); else if (_statusKey.Length > 0) workspace.Notify(T(_statusKey)); };
        workspace.CancelExportRequested += () => { _finishingCancel?.Cancel(); CancelConcept(); };
        workspace.GenerateRequested += () => _ = GenerateH3Async();
        workspace.ScheduleGenerationRequested += due => _ = ScheduleH3Async(due);
        workspace.CancelGenerationRequested += CancelH3;
        workspace.PauseGenerationRequested += () => _ = PauseH3Async();
        workspace.SendEditorRequested += async path => { if (_h3Editor is not null) await _h3Editor(path); };
        workspace.GenerationAvailable = true;
        workspace.ReloadResults();
        workspace.SaveAsRequested += () => { if (ChooseLocation()) workspace.Notify(T("H3SettingsSaved")); };
        workspace.ChangeModelRequested += () =>
        {
            if (!Checkpoint() || !ChooseFinishModel()) return;
            if (VideoGeneratorPlan.Selected(Session.Project)?.Settings.ModelId == VideoH3Catalog.ModelId) return;
            page.RequestExit(); OpenSelectedGenerator();
        };
        workspace.ExportRequested += async () =>
        {
            if (!Checkpoint()) return;
            // The existing export owns audit/background operations; this screen only hosts its state.
            try { await ExportPreparationAsync(); workspace.Notify(T(_statusKey.Length == 0 ? "H3SettingsSaved" : _statusKey)); }
            finally { workspace.SetBusy(false); }
        };
        return workspace;
    }
    private Action<VideoGeneratorSettings>? _generatorPreparation;
    public void ConfigureGeneratorPreparation(Action<VideoGeneratorSettings> prepare)
    {
        if (_generatorPreparation is not null) GeneratorRequested -= _generatorPreparation;
        _generatorPreparation = prepare; GeneratorRequested += prepare;
    }
    public void ShowGeneratorPreparationResult(bool ready)
    { _statusKey = ready ? "H3FilesReady" : "GeneratorNextPending"; RefreshStatus(); }
}
