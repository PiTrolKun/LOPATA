using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.Shorts;
using AIHub.Services.SongClip;

namespace AIHub.Controls;

internal sealed partial class ShortsControl
{
    private readonly ShortsProjects _projects;
    private ShortsProject _project;
    private bool _migrateLegacyDraft;
    private readonly TextBlock _projectName = new() { TextWrapping = TextWrapping.Wrap, Margin = new(10, 6, 0, 6) };

    private void AddProjectControls()
    {
        var row = new DockPanel();
        var manage = Action("Shorts.Projects.Manage", "Projects.Manage", ManageProjectsAsync);
        DockPanel.SetDock(manage, Dock.Left);
        row.Children.Add(manage);
        _projectName.VerticalAlignment = System.Windows.VerticalAlignment.Center;
        AutomationProperties.SetAutomationId(_projectName, "Shorts.Projects.Current");
        row.Children.Add(_projectName);
        _inputs.Children.Add(row);
    }

    private string ProjectName(ShortsProject project) => !string.IsNullOrWhiteSpace(project.Name) ? project.Name
        : project.Workspace.FromSong && !string.IsNullOrWhiteSpace(project.Workspace.Song)
            ? Path.GetFileNameWithoutExtension(project.Workspace.Song)
        : project.Persistent ? Format("Shorts.Projects.DefaultName", project.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss"))
        : ProjectText("New");

    private bool ProjectsBusy => _busy || Pending is not null;

    private void RequireProjectIdle()
    {
        if (ProjectsBusy) throw new InvalidOperationException("Music.Projects.Busy");
    }

    private string ProjectText(string key)
    {
        var localKey = "Shorts.Projects." + key;
        var text = _localize(localKey);
        return text == localKey ? _localize("Music.Projects." + key) : text;
    }

    private ScenarioProjectRow ProjectRow(ShortsProject project) =>
        new(project.Id, ProjectName(project), project.CreatedAt, project.Steps.Length, project.Manual, project.Persistent);

    private Task ManageProjectsAsync()
    {
        RequireProjectIdle();
        var commands = new ScenarioProjectCommands("Shorts.Projects", ProjectText, () => ProjectsBusy, () => ProjectRow(_project),
            () => _projects.List().Select(ProjectRow).ToArray(), FixCurrentProject, NewProject, OpenProject, RenameProject,
            History: ProjectHistory, ErrorText: _localize);
        new ScenarioProjectsWindow(commands) { Owner = Window.GetWindow(this) }.ShowDialog();
        return Task.CompletedTask;
    }

    private void FixCurrentProject()
    {
        RequireProjectIdle();
        _project = _projects.SaveWorkspace(_project, _draft, manual: true);
        _draft = _project.Workspace;
        SaveProjectDraft();
    }

    private void RenameProject(string name)
    {
        RequireProjectIdle();
        _project = _projects.Rename(_project, name);
        SaveProjectDraft(keepTemporary: true);
    }

    private void NewProject()
    {
        RequireProjectIdle();
        SaveProjectDraft(keepTemporary: true);
        var project = _projects.New();
        ActivateProject(project, project.Workspace);
        SaveProjectDraft();
    }

    private void OpenProject(string id)
    {
        RequireProjectIdle();
        SaveProjectDraft(keepTemporary: true);
        var project = _projects.Load(id);
        ActivateProject(project, project.Workspace);
        SaveProjectDraft();
    }

    private void ActivateProject(ShortsProject project, ShortsDraft draft)
    {
        StopPreview();
        _songRead?.Cancel();
        _songRead = null;
        _songSeconds = 0;
        _timeline.Waveform = null;
        _range.Text = "";
        _output = null;
        _suggestedOutput = null;
        _status.Text = "";
        _project = project;
        _draft = draft;
        LoadFields();
        if (_draft.OutputPath is null && TryReadJob(_draft.JobId) is { } job && !File.Exists(job.Output))
            _suggestedOutput = job.Output;
        SetOutputDisplay();

        ShowLatestProjectResult();

        _projectName.Text = ProjectName(_project);
        if (IsLoaded && _draft.FromSong) _ = ReadSongAsync();
    }

    private void ShowLatestProjectResult()
    {
        foreach (var id in _project.Steps.Reverse().Select(step => (string?)step.JobId).Prepend(_draft.JobId).Distinct())
            if (TryReadJob(id) is { } recent && DisplayResult(recent)) return;
    }

    private void SaveProjectDraft(bool keepTemporary = false)
    {
        _saveTimer.Stop();
        if (_project.Persistent || keepTemporary && (ShortsProjects.HasContent(_draft) || _project.Name.Length > 0))
        {
            _project = _projects.SaveWorkspace(_project, _draft);
            _draft = _project.Workspace;
        }
        else _project = _project with { Workspace = _draft };
        _projects.WriteDraft(_draft);
        _projectName.Text = ProjectName(_project);
        _migrateLegacyDraft = false;
    }

    private void RecordProjectJob(ShortsDraft draft, ShortsJob job)
    {
        if (job.ProjectId is not null && job.ProjectId != _project.Id) throw new InvalidDataException("Shorts.InvalidJob");
        _project = _projects.Record(_project, draft, job.Id, job.CreatedUtc);
        _draft = _project.Workspace;
        SaveProjectDraft();
    }

    private void RestoreJobProject(ShortsJob job)
    {
        var owner = _projects.FindOwner(job.ProjectId, job.Id);
        if (owner is null)
        {
            if (_draft.JobId != job.Id)
            {
                SaveProjectDraft(keepTemporary: true);
                var song = job.Song;
                var item = job.Description?.Items.FirstOrDefault();
                var draft = new ShortsDraft(job.FromSong, job.Seconds, item?.Prompt ?? "",
                    song is null ? "" : ShortsJob.Assets.Resolve(job.Id, song.Song),
                    References: song is not null
                        ? SongClipReferences.For(song).Select(reference =>
                            new ShortsReference(ShortsJob.Assets.Resolve(job.Id, reference.File), reference.Description)).ToArray()
                        : item?.Images.Select(image => new ShortsReference(image)).ToArray(),
                    JobId: job.Id, OutputPath: job.Output, ExcerptFrames: job.ExcerptFrames, FadeEnding: job.FadeEnding);
                var recovered = _projects.Restore(draft);
                ActivateProject(recovered, recovered.Workspace);
            }
            RecordProjectJob(_draft, job);
            return;
        }

        if (_project.Id != owner.Id || _draft.JobId != job.Id)
        {
            SaveProjectDraft(keepTemporary: true);
            var step = owner.Steps.Single(item => item.JobId == job.Id);
            ActivateProject(owner, step.Snapshot with { JobId = job.Id, OutputPath = job.Output, ProjectId = owner.Id });
        }
    }

    private IReadOnlyList<ScenarioProjectHistoryItem> ProjectHistory(string id)
    {
        var project = id == _project.Id ? _project : _projects.Load(id);
        return project.Steps.Select((step, index) =>
        {
            var job = TryReadJob(step.JobId);
            var output = job is not null && File.Exists(job.Output) ? job.Output : null;
            var phase = ApplicationBackgroundOperations.Current?.FindOperations(ShortsJob.Kind, step.JobId).FirstOrDefault()?.Phase;
            var status = output is not null ? "Completed" : phase switch
            {
                BackgroundOperationPhase.Running or BackgroundOperationPhase.Pausing => "Running",
                BackgroundOperationPhase.Paused or BackgroundOperationPhase.Waiting or BackgroundOperationPhase.Countdown => "Paused",
                BackgroundOperationPhase.Failed => "Failed",
                BackgroundOperationPhase.Canceled => "Cancelled",
                _ => "Interrupted"
            };
            var caption = string.Format(ProjectText("Step"), index + 1, project.Steps.Length) + " · "
                + (job?.CreatedUtc ?? step.CreatedAt).ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss") + " · " + ProjectText(status)
                + (output is null ? "" : " · " + Path.GetFileName(output));
            return new ScenarioProjectHistoryItem(caption,
                output is null ? null : () => PlayProjectFile(output), () => RestoreProjectStep(project.Id, step.JobId));
        }).Reverse().ToArray();
    }

    private void RestoreProjectStep(string id, string jobId)
    {
        RequireProjectIdle();
        SaveProjectDraft(keepTemporary: true);
        var project = _projects.Load(id);
        var step = project.Steps.Single(item => item.JobId == jobId);
        ActivateProject(project, step.Snapshot with { JobId = null, OutputPath = null, ProjectId = id });
        SaveProjectDraft();
    }

    private static void PlayProjectFile(string output)
    {
        if (!File.Exists(output)) throw new FileNotFoundException("Shorts.Projects.ResultMissing");
        Process.Start(new ProcessStartInfo(output) { UseShellExecute = true });
    }

    private static ShortsJob? TryReadJob(string? id)
    {
        if (id is null) return null;
        try { return ShortsJob.Load(id); }
        catch (Exception error) when (ShortsProjects.ExpectedReadError(error)) { return null; }
    }
}
