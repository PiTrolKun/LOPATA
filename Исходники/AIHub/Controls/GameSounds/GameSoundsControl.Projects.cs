using System.Windows;
using System.Windows.Controls;
using AIHub.Services;
using AIHub.Services.GameSounds;

namespace AIHub.Controls.GameSounds;

internal sealed partial class GameSoundsControl
{
    private async Task ProjectsAsync()
    {
        if (_switching || _exporting || _running is { IsCompleted: false }) { _status.Text = T("ProjectBusy"); return; }
        if (!Checkpoint()) return;
        var projects = _projects.List();
        var window = Dialog("Projects", 650, 440);
        var panel = new DockPanel { Margin = new(20) };
        var commands = Row();
        var list = new System.Windows.Controls.ListBox { Margin = new(0, 5, 0, 7) };
        ThemeInput(list);
        foreach (var project in projects)
        {
            var name = string.IsNullOrWhiteSpace(project.Name) ? T("Untitled") : project.Name;
            list.Items.Add(new ListBoxItem { Content = $"{F("ProjectCaption", project.Number, name)} · {project.CreatedUtc.ToLocalTime():g}", Tag = project.Id });
            if (project.Id == Project.Id) list.SelectedIndex = list.Items.Count - 1;
        }
        string? openId = null;
        var create = false;
        void OpenSelected()
        {
            if (list.SelectedItem is not ListBoxItem { Tag: string id }) return;
            openId = id; window.DialogResult = true;
        }
        commands.Children.Add(Action("NewProject", () => { create = true; window.DialogResult = true; }));
        commands.Children.Add(Action("OpenProject", OpenSelected));
        commands.Children.Add(Action("RenameProject", () =>
        {
            var id = (list.SelectedItem as ListBoxItem)?.Tag as string;
            var target = id is null || id == Project.Id ? Project : _projects.Load(id);
            var rename = Dialog("RenameProject", 485, 160); rename.Owner = window;
            var fields = new StackPanel { Margin = new(12) };
            var name = Input(target.Name, "ProjectName"); fields.Children.Add(name);
            fields.Children.Add(MusicWishUi.Footer(_localize, () => rename.DialogResult = true, () => rename.DialogResult = false));
            rename.Content = fields; name.Focus();
            if (rename.ShowDialog() != true) return;
            target.Name = name.Text.Trim();
            _projects.Save(target);
            if (ReferenceEquals(target, Project)) UpdateTitle();
            window.DialogResult = false;
        }));
        commands.Children.Add(Action("Cancel", () => window.DialogResult = false));
        DockPanel.SetDock(commands, Dock.Bottom); panel.Children.Add(commands);
        panel.Children.Add(list); window.Content = panel;
        list.MouseDoubleClick += (_, _) => OpenSelected();
        if (window.ShowDialog() != true || !create && openId == Project.Id) return;
        if (create)
        {
            var currentOptions = Project.Options;
            Project = NewProject(); Project.Options = currentOptions;
            _dirty = false; _previous.Clear(); Render();
        }
        else if (openId is not null) await OpenProjectAsync(openId);
    }

    internal async Task OpenProjectAsync(string id, bool fromTray = false)
    {
        if (_switching || !Checkpoint()) return;
        var project = _projects.Load(id);
        var changedModel = _model.Session is null || Project.Options.ModelId != project.Options.ModelId ||
            Project.Options.Placement != project.Options.Placement;
        if (!_entered) { LoadCards(); changedModel = true; }
        if (changedModel)
        {
            _switching = true; UpdateCommands();
            try
            {
                await _model.RetireAsync();
                if (!Prepare(project.Options)) return;
                await WarmAsync(project.Options, initial: true, ApplicationBackgroundOperations.ExitToken);
            }
            finally { _switching = false; UpdateCommands(); }
        }
        _entered = true;
        Project = project; _dirty = false; _previous.Clear(); Render();
        if (Project.Pending is not null) _status.Text = T("HasPending");
        if (fromTray) _chatScroll.ScrollToEnd();
    }
}
