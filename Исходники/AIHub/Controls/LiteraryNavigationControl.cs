using System.Windows;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using UserControl = System.Windows.Controls.UserControl;
using Orientation = System.Windows.Controls.Orientation;

namespace AIHub.Controls;

public sealed class LiteraryNavigationControl : UserControl
{
    private Func<string, string> _l = key => key;
    private readonly LiteraryProjectSelection _projects = new();
    private readonly LiteraryProjectStore _store = LiteraryProjectStore.Default();
    private string _language = "ru", _initialFolder = "", _notice = "";
    private LiteraryProjectCreateControl? _creation;
    private LiteraryWorkspaceControl? _workspace;
    public bool ShowingProjects { get; private set; }
    public event Action? BackRequested;
    public event Action? HomeRequested;
    public bool CanLeave() => _workspace?.CanLeave() ?? true;

    public void Configure(Func<string, string> localize, bool projects = false, string? language = null, string? initialFolder = null)
    {
        _l = localize;
        ShowingProjects = projects;
        if (language is not null) _language = language;
        if (initialFolder is not null) _initialFolder = initialFolder;
        LoadProjects();
        _workspace?.ApplyLocalization(localize);
        Render();
    }

    public void GoBack()
    {
        if (_workspace is not null) { if (!CanLeave()) return; _workspace = null; ShowingProjects = true; Render(); return; }
        if (_creation is not null) { if (!_creation.IsSaving) { _creation = null; Render(); } return; }
        if (ShowingProjects) { ShowingProjects = false; Render(); }
        else BackRequested?.Invoke();
    }

    private void Render()
    {
        if (_workspace is not null) { Content = _workspace; return; }
        if (_creation is not null) { Content = _creation; return; }
        var root = new Grid { Margin = new Thickness(56, 36, 56, 28) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = LiteraryUi.Text(_l(ShowingProjects ? "Literary.Work" : "Literary.Title"), true);
        heading.SetResourceReference(TextBlock.FontSizeProperty, "UiPageTitleFontSize");
        heading.Margin = new Thickness(0, 0, 0, 20);
        root.Children.Add(heading);
        var body = new StackPanel();
        Grid.SetIsSharedSizeScope(body, true);
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        if (!ShowingProjects)
        {
            body.Children.Add(Card("Literary.Import", "Literary.ImportHint", "Literary.Pending", null));
            body.Children.Add(Card("Literary.Work", "Literary.WorkHint", "Literary.Start", () => { ShowingProjects = true; Render(); }));
        }
        else
        {
            body.Children.Add(LiteraryUi.Text(_l("Literary.Active") + " " + (_projects.ActiveProject?.Title ?? _l("Literary.None"))));
            body.Children.Add(MenuButton("Literary.New", null, CreateProject));
            body.Children.Add(MenuButton("Literary.Continue", null, _projects.ActiveProject is { } active ? () => OpenWorkspace(active) : null));
            body.Children.Add(MenuButton("Literary.Select", null, () => OpenProjects(LiteraryProjectDialogMode.Select)));
            body.Children.Add(MenuButton("Literary.SelectActive", null, () => OpenProjects(LiteraryProjectDialogMode.Active)));
            body.Children.Add(MenuButton("Literary.Export", null, () => OpenProjects(LiteraryProjectDialogMode.Export)));
            if (_notice.Length > 0) body.Children.Add(LiteraryUi.Text(_notice));
        }
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 20, 0, 0) };
        footer.Children.Add(LiteraryUi.Button(_l("Literary.Back"), GoBack));
        footer.Children.Add(LiteraryUi.Button(_l("Literary.Home"), () => HomeRequested?.Invoke()));
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        Content = root;
    }

    private FrameworkElement MenuButton(string key, string? hint, Action? action)
    {
        var row = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "ProjectActions" });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        var button = LiteraryUi.Button(_l(key), action);
        button.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        row.Children.Add(button);
        if (hint is not null)
        {
            var description = LiteraryUi.Text(_l(hint));
            Grid.SetColumn(description, 1);
            row.Children.Add(description);
        }
        return row;
    }

    private Border Card(string title, string hint, string label, Action? action)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { Margin = new Thickness(0, 0, 20, 0) };
        text.Children.Add(LiteraryUi.Text(_l(title), true));
        text.Children.Add(LiteraryUi.Text(_l(hint)));
        grid.Children.Add(text);
        var button = LiteraryUi.Button(_l(label), action, true);
        Grid.SetColumn(button, 1);
        grid.Children.Add(button);
        var border = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(20), Margin = new Thickness(0, 0, 0, 14), Child = grid };
        border.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        border.SetResourceReference(Border.BorderBrushProperty, action is null ? "LineBrush" : "AccentBrush");
        return border;
    }

    private void OpenProjects(LiteraryProjectDialogMode mode)
    {
        LoadProjects();
        var dialog = new LiteraryProjectDialog(_projects, _l, mode, id => _store.SetActive(id)) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true && dialog.SelectedProject is { } selected)
        {
            if (mode == LiteraryProjectDialogMode.Export) _ = LiteraryExportDialog.ShowAsync(this, _l, selected.ProjectPath, selected.Title);
            else OpenWorkspace(selected);
        }
        Render();
    }

    private void LoadProjects()
    {
        try
        {
            var index = _store.Load();
            _projects.SetProjects(index.Projects);
            if (index.ActiveId is not null && index.Projects.Any(p => p.Id == index.ActiveId)) _projects.SetActive(index.ActiveId);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { _notice = _l("Literary.Create.LoadError") + " " + ex.Message; }
    }

    private void CreateProject()
    {
        _creation = new LiteraryProjectCreateControl(_l, _language, _initialFolder, _store);
        _creation.CancelRequested += GoBack;
        _creation.ProjectCreated += entry =>
        {
            LoadProjects();
            _notice = _l("Literary.Create.Created") + " " + entry.ProjectPath;
            _creation = null;
            ShowingProjects = true;
            OpenWorkspace(entry);
        };
        Render();
    }

    private void OpenWorkspace(LiteraryProjectEntry entry)
    {
        try
        {
            var project = LiteraryProjectStore.ReadProject(entry.ProjectPath);
            _workspace = new LiteraryWorkspaceControl(entry, project, _l);
            _workspace.BackRequested += GoBack;
            _workspace.HomeRequested += () => { _workspace = null; HomeRequested?.Invoke(); };
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { _notice = _l("Literary.Create.LoadError") + " " + ex.Message; }
        Render();
    }
}

internal static class LiteraryUi
{
    public static TextBlock Text(string text, bool title = false)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 0, 4) };
        block.SetResourceReference(TextBlock.ForegroundProperty, title ? "TextPrimaryBrush" : "TextSecondaryBrush");
        block.SetResourceReference(TextBlock.FontSizeProperty, title ? "UiCardTitleFontSize" : "UiBodyFontSize");
        if (title) block.FontWeight = FontWeights.SemiBold;
        return block;
    }

    public static Button Button(string text, Action? action, bool primary = false)
    {
        var button = new Button { Content = text, IsEnabled = action is not null, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        button.SetResourceReference(FrameworkElement.StyleProperty, primary ? "PrimaryButtonStyle" : "SecondaryButtonStyle");
        if (action is not null) button.Click += (_, _) => action();
        return button;
    }
}
