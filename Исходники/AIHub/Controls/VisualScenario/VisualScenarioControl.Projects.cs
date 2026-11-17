using AIHub.Services.VisualScenario;

namespace AIHub.Controls.VisualScenario;

internal sealed partial class VisualScenarioControl
{
    internal string? RecoveryProjectId => _project.HasWork || _project.Persistent ? _project.Id : null;
    private string ProjectName(VisualProject project) => string.IsNullOrWhiteSpace(project.Name)
        ? (project.Persistent ? project.CreatedAt.LocalDateTime.ToString("g") : _localize("Music.Projects.New")) : project.Name;
    private ScenarioProjectRow ProjectRow(VisualProject project) =>
        new(project.Id, ProjectName(project), project.CreatedAt, project.Results.Count, project.Manual, project.Persistent);

    private void OpenProjects()
    {
        if (_busy || Pending is not null) return;
        var commands = new ScenarioProjectCommands("Visual.Projects", key => _localize("Music.Projects." + key),
            () => _busy || Pending is not null, () => ProjectRow(_project), () => _projects.List().Select(ProjectRow).ToArray(),
            () => { _project.Manual = _project.Persistent = true; Save(); },
            NewProject, RestoreProject,
            name => { _project.Name = name.Trim(); _project.Persistent = true; Save(); },
            History: id => _projects.Load(id).Results.AsEnumerable().Reverse().Select(result =>
                new ScenarioProjectHistoryItem(result.At.LocalDateTime.ToString("g") + " · " + result.Prompt.Text,
                    () => OpenImage(result.Result), () =>
                    {
                        RestoreProject(id);
                        ShowSavedPrompt(result);
                    })).ToArray(), ErrorText: _localize);
        new ScenarioProjectsWindow(commands) { Owner = OwnerWindow }.ShowDialog();
        Render();
    }

    private void NewProject()
    {
        var advisor = _project.Advisor;
        var artist = _project.Artist;
        SaveBeforeSwitch();
        _project = new() { Advisor = advisor, Artist = artist };
        _projects.Save(_project);
        ResetWorkspace();
    }

    internal void RestoreProject(string id)
    {
        if (_project.Id == id) return;
        SaveBeforeSwitch();
        _project = _projects.Load(id);
        _projects.Save(_project);
        ResetWorkspace();
    }

    private void SaveBeforeSwitch()
    {
        if (_project.HasWork) _project.Persistent = true;
        if (!Save()) throw new System.IO.IOException("Visual.ProjectSaveFailed");
    }

    private void ResetWorkspace()
    {
        RunUi(async () =>
        {
            await _artistSession.RetireAsync();
            if (_project.Advisor != VisualSenseNova.Id) await _senseNova.RetireAsync();
            else WarmAdvisorOnEntry();
        });
        _settings.IsExpanded = !_project.Messages.Any(message => message.User);
        _contextRejected = false;
        _meter = null;
        _status.Text = "";
        Render();
    }
}
