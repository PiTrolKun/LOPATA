using System.Windows;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using UserControl = System.Windows.Controls.UserControl;
using Panel = System.Windows.Controls.Panel;

namespace AIHub.Controls;

public sealed class LiteraryWorkspaceControl : UserControl
{
    private readonly LiteraryProjectEntry _entry;
    private readonly LiteraryProject _project;
    private Func<string, string> _l;
    private readonly LiteraryChatControl _writer;
    private readonly LiteraryChatControl _advisor;
    private readonly LiteraryChatRuntime _runtime = new();
    private readonly LiteraryDraftControl _draft;
    public event Action? BackRequested;
    public event Action? HomeRequested;
    public event Action<LiteraryWorkspaceAction>? ActionRequested;
    // Independent hosts allow future implementations to be attached without rebuilding the page layout.
    public ContentControl EditorHost { get; } = new();
    public ContentControl WriterHost { get; } = new();
    public ContentControl TreeHost { get; } = new();
    public ContentControl AdvisorHost { get; } = new();

    public LiteraryWorkspaceControl(LiteraryProjectEntry entry, LiteraryProject project, Func<string, string> localize)
    {
        _entry = entry; _project = project; _l = localize;
        _draft = new LiteraryDraftControl(entry.ProjectPath, localize);
        _writer = new LiteraryChatControl(localize, _runtime, LiteraryChatProfile.Writer, () => _draft.Text, project);
        _advisor = new LiteraryChatControl(localize, _runtime, LiteraryChatProfile.Advisor, () => _draft.Text, project);
        Unloaded += (_, _) => { _draft.Save(); _runtime.Stop(); if (System.Windows.Application.Current is { } app) app.Exit -= OnAppExit; };
        Loaded += (_, _) => { if (System.Windows.Application.Current is { } app) { app.Exit -= OnAppExit; app.Exit += OnAppExit; } };
        IsVisibleChanged += (_, _) => { if (!IsVisible) { _draft.Save(); _runtime.Stop(); } };
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/LiteraryScrollResources.xaml", UriKind.Relative) });
        Render();
    }

    private void OnAppExit(object sender, ExitEventArgs e) { _draft.Save(); _runtime.Stop(); }
    public void ApplyLocalization(Func<string, string> localize) { _l = localize; _draft.ApplyLocalization(localize); _writer.ApplyLocalization(localize); _advisor.ApplyLocalization(localize); Render(); }

    private Button PendingButton(string label, LiteraryWorkspaceAction action)
    {
        var button = LiteraryUi.Button(label, () => ActionRequested?.Invoke(action));
        button.IsEnabled = false;
        if (action is LiteraryWorkspaceAction.RenameChapter or LiteraryWorkspaceAction.WriterAttach or LiteraryWorkspaceAction.AdvisorAttach or LiteraryWorkspaceAction.WriterSend or LiteraryWorkspaceAction.AdvisorSend)
        {
            button.MinWidth = 0; button.Width = 34; button.Padding = new Thickness(4);
        }
        button.ToolTip = _l("Literary.Pending");
        ToolTipService.SetShowOnDisabled(button, true);
        System.Windows.Automation.AutomationProperties.SetName(button, _l("Literary.Workspace.Action." + action));
        return button;
    }

    private void Render()
    {
        foreach (var host in new[] { EditorHost, WriterHost, TreeHost, AdvisorHost })
            if (host.Parent is Panel parent) parent.Children.Remove(host);
        var root = new Grid { Margin = new Thickness(20, 18, 20, 16), MinHeight = 490, MinWidth = 740 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.43, GridUnitType.Star), MinHeight = 185 });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.57, GridUnitType.Star), MinHeight = 220 });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var breadcrumb = LiteraryUi.Text(_entry.Title + "  ›  " + _project.WorkTitle);
        breadcrumb.ToolTip = _entry.ProjectPath; breadcrumb.Margin = new Thickness(0, 0, 0, 10);
        root.Children.Add(breadcrumb);
        EditorHost.Content = BuildEditor(); Grid.SetRow(EditorHost, 1); root.Children.Add(EditorHost);
        var vertical = new GridSplitter { Height = 6, HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center, ResizeDirection = GridResizeDirection.Rows, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        vertical.SetResourceReference(BackgroundProperty, "LineBrush");
        Grid.SetRow(vertical, 2); root.Children.Add(vertical);
        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 210 });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.8, GridUnitType.Star), MinWidth = 160 });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 210 });
        WriterHost.Content = _writer;
        AdvisorHost.Content = _advisor;
        var tree = new StackPanel(); tree.Children.Add(LiteraryUi.Text(_l("Literary.Workspace.Tree"), true));
        tree.Children.Add(LiteraryUi.Text(_l("Literary.Workspace.TreeHint")));
        TreeHost.Content = LiteraryWorkspaceParts.Card(tree);
        foreach (var (host, index) in new[] { (WriterHost, 0), (TreeHost, 2), (AdvisorHost, 4) }) { Grid.SetColumn(host, index); columns.Children.Add(host); }
        foreach (var index in new[] { 1, 3 })
        {
            var splitter = new GridSplitter { Width = 6, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Stretch, ResizeDirection = GridResizeDirection.Columns, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
            splitter.SetResourceReference(BackgroundProperty, "LineBrush"); Grid.SetColumn(splitter, index); columns.Children.Add(splitter);
        }
        Grid.SetRow(columns, 3); root.Children.Add(columns);
        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var navigation = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        navigation.Children.Add(LiteraryUi.Button(_l("Literary.Back"), () => BackRequested?.Invoke()));
        navigation.Children.Add(LiteraryUi.Button(_l("Literary.Home"), () => HomeRequested?.Invoke()));
        DockPanel.SetDock(navigation, Dock.Left); footer.Children.Add(navigation);
        footer.Children.Add(LiteraryUi.Text(_l("Literary.Writer.Footer") + " · " + _l("Literary.Form." + _project.Form)));
        Grid.SetRow(footer, 4); root.Children.Add(footer);
        var scroll = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        // Constrain star-sized panels to the viewport; scrolling alone measures content at infinity.
        scroll.SizeChanged += (_, _) =>
        {
            root.Width = Math.Max(740, scroll.ActualWidth - 40);
            root.Height = Math.Max(490, scroll.ActualHeight - 34);
        };
        Content = scroll;
    }

    private UIElement BuildEditor()
    {
        if (_draft.Parent is Panel previous) previous.Children.Remove(_draft);
        var panel = new DockPanel();
        var header = new WrapPanel();
        var title = LiteraryUi.Text(_l("Literary.Workspace.Chapter"), true); title.Margin = new Thickness(0, 0, 14, 4); header.Children.Add(title);
        header.Children.Add(PendingButton("✎", LiteraryWorkspaceAction.RenameChapter));
        header.Children.Add(PendingButton(_l("Literary.Workspace.History"), LiteraryWorkspaceAction.History));
        header.Children.Add(PendingButton(_l("Literary.Export"), LiteraryWorkspaceAction.Export));
        header.Children.Add(PendingButton(_l("Literary.Workspace.Finish"), LiteraryWorkspaceAction.FinishChapter));
        DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header);
        var bottom = new WrapPanel();
        foreach (var key in new[] { "Literary.Workspace.Style", "Literary.Workspace.Mode" })
        {
            var combo = new System.Windows.Controls.ComboBox { IsEnabled = false, MinWidth = 120, Margin = new Thickness(12, 4, 0, 0), ToolTip = _l("Literary.Pending") };
            combo.Items.Add(_l(key)); combo.SelectedIndex = 0; bottom.Children.Add(combo);
        }
        DockPanel.SetDock(bottom, Dock.Bottom); panel.Children.Add(bottom);
        panel.Children.Add(_draft);
        return LiteraryWorkspaceParts.Card(panel);
    }
}
