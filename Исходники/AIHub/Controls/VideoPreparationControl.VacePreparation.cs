using AIHub.Services.VideoPreparation;

namespace AIHub.Controls;

public sealed partial class VideoPreparationControl
{
    private VideoVacePreparationControl? _vacePage;

    internal VideoVacePreparationControl ShowVacePreparation(string root, int connections,
        Func<IVideoVacePreparation>? create = null)
    {
        var page = new VideoVacePreparationControl(_localize, root, connections, create);
        page.CreateWorkspace = () => CreateVaceWorkspace(page);
        page.BackRequested += () =>
        {
            if (!ReferenceEquals(page, _vacePage)) return;
            _vacePage = null;
            Render();
        };
        page.HomeRequested += () =>
        {
            if (ReferenceEquals(page, _vacePage) && Checkpoint()) HomeRequested?.Invoke();
        };
        _vacePage = page;
        Render();
        return page;
    }

    private VideoVaceWorkspaceControl CreateVaceWorkspace(VideoVacePreparationControl page)
    {
        var workspace = new VideoVaceWorkspaceControl(_localize, Session.Project, () =>
        { Session.Changed(); _autosave.Stop(); _autosave.Start(); });
        workspace.GenerationAvailable = true;
        if (AIHub.Services.ApplicationBackgroundOperations.Current?.State is
            { Kind: VideoVaceJob.Kind, Phase: AIHub.Models.BackgroundOperationPhase.Paused or AIHub.Models.BackgroundOperationPhase.Waiting or AIHub.Models.BackgroundOperationPhase.Countdown })
            workspace.SetGenerating(false, pending: true);
        workspace.SaveRequested += () => { if (Save()) workspace.Notify(T("H3SettingsSaved")); };
        workspace.SaveAsRequested += () => { if (ChooseLocation()) workspace.Notify(T("H3SettingsSaved")); };
        workspace.GenerateRequested += () => _ = GenerateVaceAsync();
        workspace.ScheduleGenerationRequested += due => _ = GenerateVaceAsync(due.ToUniversalTime());
        workspace.CancelGenerationRequested += CancelVace;
        workspace.PauseGenerationRequested += () => _ = PauseVaceAsync();
        workspace.CancelExportRequested += () => { _finishingCancel?.Cancel(); CancelConcept(); };
        workspace.SendEditorRequested += async path => { if (_vaceEditor is not null) await _vaceEditor(path); };
        workspace.ChangeModelRequested += () =>
        {
            if (!Checkpoint() || !ChooseFinishModel()) return;
            if (VideoGeneratorPlan.Selected(Session.Project)?.Settings.ModelId == VideoVaceCatalog.ModelId) return;
            page.RequestExit(); OpenSelectedGenerator();
        };
        workspace.ExportRequested += async () =>
        {
            if (!Checkpoint()) return;
            try { await ExportPreparationAsync(); workspace.Notify(T(_statusKey.Length == 0 ? "H3SettingsSaved" : _statusKey)); }
            finally { workspace.SetBusy(false); }
        };
        return workspace;
    }
}
