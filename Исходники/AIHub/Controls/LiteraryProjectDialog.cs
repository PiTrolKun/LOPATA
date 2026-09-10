using System.Windows;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using ComboBox = System.Windows.Controls.ComboBox;
using ListBox = System.Windows.Controls.ListBox;

namespace AIHub.Controls;

public enum LiteraryProjectDialogMode { Select, Active, Export }

public sealed class LiteraryProjectDialog : Window
{
    public LiteraryProjectEntry? SelectedProject { get; private set; }
    private bool _removing;
    public LiteraryProjectDialog(LiteraryProjectSelection projects, Func<string, string> l, LiteraryProjectDialogMode mode,
        Action<string>? saveActive = null, Func<LiteraryProjectEntry, LiteraryProjectRemoval, Task>? remove = null)
    {
        Title = l(mode switch { LiteraryProjectDialogMode.Active => "Literary.SelectActive", LiteraryProjectDialogMode.Export => "Literary.Export", _ => "Literary.Select" });
        Width = 720; Height = 480; MinWidth = 520; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "PanelBrush");
        // Main-window resources are shared with owned dialogs, including the current theme.
        Loaded += (_, _) => { if (Owner is not null) Resources = Owner.Resources; };
        var root = new DockPanel { Margin = new Thickness(24) };
        Closing += (_, e) => { if (_removing) e.Cancel = true; };
        var footer = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        var close = LiteraryUi.Button(l("Literary.Close"), Close);
        close.IsCancel = true;
        close.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
        footer.Children.Add(LiteraryUi.Text(l(mode == LiteraryProjectDialogMode.Active ? "Literary.StarHint" : "Literary.SelectionHint")));
        footer.Children.Add(close);
        if (projects.Projects.Count == 0)
        {
            root.Children.Add(LiteraryUi.Text(l("Literary.Empty")));
        }
        else
        {
            var list = new ListBox { HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch };
            if (mode is LiteraryProjectDialogMode.Select or LiteraryProjectDialogMode.Export)
            {
                var open = LiteraryUi.Button(l(mode == LiteraryProjectDialogMode.Export ? "Literary.Export" : "Literary.Workspace.Open"), () =>
                {
                    SelectedProject = projects.Projects.FirstOrDefault(p => p.Id == (list.SelectedItem as ListBoxItem)?.Tag as string);
                    if (SelectedProject is not null) DialogResult = true;
                });
                open.IsEnabled = false;
                list.SelectionChanged += (_, _) => open.IsEnabled = list.SelectedItem is not null;
                footer.Children.Insert(0, open);
            }
            list.SetResourceReference(BackgroundProperty, "PanelBrush");
            list.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
            void Refresh()
            {
                var selected = (list.SelectedItem as ListBoxItem)?.Tag;
                list.Items.Clear();
                foreach (var project in projects.Projects)
                {
                    var row = new DockPanel { Margin = new Thickness(6) };
                    if (mode != LiteraryProjectDialogMode.Export)
                    {
                        if (remove is not null)
                        {
                            var delete = LiteraryUi.Button("×", null);
                            delete.IsEnabled = true;
                            delete.MinWidth = 32; delete.Padding = new Thickness(6);
                            delete.ToolTip = l("Literary.Delete.Title");
                            System.Windows.Automation.AutomationProperties.SetName(delete, l("Literary.Delete.Title") + ": " + project.Title);
                            delete.Click += async (_, _) =>
                            {
                                var choice = LiteraryEditorDialogs.Choose(this, l, "Literary.Delete.Title",
                                    string.Format(l("Literary.Delete.Question"), project.Title, project.ProjectPath),
                                    "Literary.Delete.Keep", "Literary.Delete.Files", "Literary.Editor.Cancel");
                                if (choice is not (0 or 1)) return;
                                _removing = true; root.IsEnabled = false;
                                try
                                {
                                    await remove(project, choice == 0 ? LiteraryProjectRemoval.KeepFiles : LiteraryProjectRemoval.DeleteFiles);
                                    projects.SetProjects(projects.Projects.Where(p => p.Id != project.Id).ToArray());
                                    Refresh();
                                }
                                catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
                                {
                                    System.Windows.MessageBox.Show(this,
                                        l(choice == 0 ? "Literary.Delete.KeepError" : "Literary.Delete.FilesError") + "\n" + project.ProjectPath + "\n" + ex.Message, Title);
                                }
                                finally { _removing = false; root.IsEnabled = true; }
                            };
                            DockPanel.SetDock(delete, Dock.Right); row.Children.Add(delete);
                        }
                        var star = LiteraryUi.Button(project.Id == projects.ActiveId ? "★" : "☆", () =>
                        {
                            try { saveActive?.Invoke(project.Id); projects.SetActive(project.Id); Refresh(); }
                            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                            { System.Windows.MessageBox.Show(this, l("Literary.Create.SaveError") + "\n" + ex.Message, Title); }
                        });
                        star.MinWidth = 32;
                        star.Padding = new Thickness(6);
                        star.ToolTip = l("Literary.SelectActive");
                        System.Windows.Automation.AutomationProperties.SetName(star, l("Literary.SelectActive") + ": " + project.Title);
                        DockPanel.SetDock(star, Dock.Right);
                        row.Children.Add(star);
                    }
                    row.Children.Add(LiteraryUi.Text(project.Title));
                    var item = new ListBoxItem { Content = row, Tag = project.Id };
                    list.Items.Add(item);
                    if (Equals(selected, project.Id)) list.SelectedItem = item;
                }
            }
            Refresh();
            root.Children.Add(list);
        }
        Content = root;
    }
}
