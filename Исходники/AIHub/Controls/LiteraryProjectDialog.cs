using System.Windows;
using System.Windows.Controls;
using AIHub.Models;
using ComboBox = System.Windows.Controls.ComboBox;
using ListBox = System.Windows.Controls.ListBox;

namespace AIHub.Controls;

public enum LiteraryProjectDialogMode { Select, Active, Export }

public sealed class LiteraryProjectDialog : Window
{
    public LiteraryProjectEntry? SelectedProject { get; private set; }
    public LiteraryProjectDialog(LiteraryProjectSelection projects, Func<string, string> l, LiteraryProjectDialogMode mode, Action<string>? saveActive = null)
    {
        Title = l(mode switch { LiteraryProjectDialogMode.Active => "Literary.SelectActive", LiteraryProjectDialogMode.Export => "Literary.Export", _ => "Literary.Select" });
        Width = 720; Height = 480; MinWidth = 520; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "PanelBrush");
        // Main-window resources are shared with owned dialogs, including the current theme.
        Loaded += (_, _) => { if (Owner is not null) Resources = Owner.Resources; };
        var root = new DockPanel { Margin = new Thickness(24) };
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
