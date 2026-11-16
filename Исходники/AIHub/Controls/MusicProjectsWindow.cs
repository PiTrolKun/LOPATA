using AIHub.Models;

namespace AIHub.Controls;

public sealed class MusicProjectsWindow : ScenarioProjectsWindow
{
    public MusicProjectsWindow(MusicProjectController projects, Func<string, string> localize)
        : base(Commands(projects)) { }

    private static ScenarioProjectCommands Commands(MusicProjectController projects)
    {
        ScenarioProjectRow Map(MusicProject project) =>
            new(project.Id, projects.Name(project), project.CreatedAt, project.Steps.Length, project.Manual, project.Persistent);

        return new("Music.Projects", projects.Text, () => projects.Busy, () => Map(projects.Current),
            () => projects.List().Select(Map).ToArray(), projects.Fix, projects.New, projects.Open, projects.Rename,
            Details: id =>
            {
                var project = id == projects.Current.Id ? projects.Current : projects.List().Single(item => item.Id == id);
                return string.Join("\n", project.Steps.Reverse().Select(step =>
                    string.Format(projects.Text("Step"), step.Number, project.Steps.Length) + " · "
                    + step.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss") + " · " + projects.Text(step.Outcome.ToString())
                    + (step.Message.Length > 0 ? " — " + step.Message : "")));
            });
    }
}
