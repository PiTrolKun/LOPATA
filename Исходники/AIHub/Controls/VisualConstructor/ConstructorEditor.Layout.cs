using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using Button = System.Windows.Controls.Button;
using Control = System.Windows.Controls.Control;
using Panel = System.Windows.Controls.Panel;
using ScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls.VisualConstructor;

internal sealed partial class ConstructorEditor
{
    private readonly List<(TextBlock Label, string Key)> _labels = [];
    private readonly Button _back = new();
    internal event Action? BackRequested;

    private UIElement Layout()
    {
        var root = new DockPanel { Margin = new Thickness(14, 10, 14, 8) };
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        var heading = Label("Title", new Thickness(0, 0, 0, 6));
        heading.FontWeight = FontWeights.SemiBold;
        heading.FontSize = 24;
        header.Children.Add(heading);
        var commands = new WrapPanel();
        header.Children.Add(commands);
        Add(commands, "New", NewAsync, entry: true);
        Add(commands, "Open", () => { Open(); return Task.CompletedTask; }, entry: true);
        _recent.Width = 176;
        _recent.Margin = new Thickness(2, 0, 12, 4);
        _recent.VerticalAlignment = VerticalAlignment.Top;
        _recent.SetResourceReference(ItemsControl.ItemTemplateProperty, "ConstructorRecentLabel");
        AutomationProperties.SetName(_recent, T("Recent"));
        AutomationProperties.SetAutomationId(_recent, "VisualConstructor.Recent");
        _recent.SelectionChanged += (_, _) =>
        {
            if (_loading || _operation is not null || _recent.SelectedValue is not string { Length: > 0 } path) return;
            _recent.SelectedIndex = 0;
            try { if (LeaveDraft()) View(Services.VisualConstructor.ConstructorProject.Open(path)); }
            catch (Exception ex) { ShowError(ex); }
        };
        commands.Children.Add(_recent);
        Add(commands, "Save", () => { Save(); return Task.CompletedTask; });
        Add(commands, "SaveAs", SaveAsAsync);
        Add(commands, "Folder", () =>
        {
            Process.Start(new ProcessStartInfo(_project!.DirectoryPath) { UseShellExecute = true })?.Dispose();
            return Task.CompletedTask;
        });
        Add(commands, "GemmaHistory", BrowseGemmaHistoryAsync);
        Add(commands, "Researcher", OpenResearcherAsync, entry: true);
        _projectPath.Text = T("StartHint");
        _projectPath.VerticalAlignment = VerticalAlignment.Center;
        _projectPath.Margin = new Thickness(8, 0, 0, 4);
        _projectPath.MaxWidth = 370;
        _projectPath.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        AutomationProperties.SetAutomationId(_projectPath, "VisualConstructor.ProjectPath");
        commands.Children.Add(_projectPath);

        var footer = new StackPanel { Margin = new Thickness(0, 7, 0, 0) };
        var footerCommands = new DockPanel();
        var build = new WrapPanel();
        DockPanel.SetDock(build, Dock.Right);
        footerCommands.Children.Add(build);
        var run = Add(build, "Run", RunAsync);
        run.FontWeight = FontWeights.SemiBold;
        Add(build, "IsolatedRun", RunIsolatedAsync);
        Add(build, "CheckBuild", CheckBuildAsync);
        Add(build, "Export", ExportAsync);
        Configure(_cancel, "Cancel");
        _cancel.Click += (_, _) => _operation?.Cancel();
        build.Children.Add(_cancel);
        Configure(_back, "Back");
        _back.Click += (_, _) => BackRequested?.Invoke();
        build.Children.Add(_back);
        CommandBindings.Add(new CommandBinding(NavigationCommands.BrowseBack, (_, _) => BackRequested?.Invoke()));
        var editing = new WrapPanel();
        editing.Children.Add(_stepCommands);
        editing.Children.Add(_gemmaCommands);
        footerCommands.Children.Add(editing);
        _state.Margin = new Thickness(0, 0, 0, 4);
        var statusStyle = new Style(typeof(TextBlock));
        var emptyStatus = new Trigger { Property = TextBlock.TextProperty, Value = "" };
        emptyStatus.Setters.Add(new Setter(VisibilityProperty, Visibility.Collapsed));
        statusStyle.Triggers.Add(emptyStatus);
        _state.Style = statusStyle;
        AutomationProperties.SetAutomationId(_state, "VisualConstructor.Status");
        footer.Children.Add(_state);
        footer.Children.Add(footerCommands);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var workspace = new DockPanel();
        root.Children.Add(workspace);
        BuildFormToolbar();
        DockPanel.SetDock(_formTools, Dock.Top);
        workspace.Children.Add(_formTools);
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(204) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(216) });
        workspace.Children.Add(body);
        var elementsPane = BuildLayoutTools();
        body.Children.Add(elementsPane);
        var work = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(10) };
        work.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        work.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        Grid.SetColumn(work, 1);
        body.Children.Add(work);
        work.Child = BuildPreview();

        var propertiesScroll = new ScrollViewer { Content = _properties, Margin = new Thickness(14, 0, 0, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetAutomationId(propertiesScroll, "VisualConstructor.WindowProperties");
        Grid.SetColumn(propertiesScroll, 2);
        body.Children.Add(propertiesScroll);
        AddPanelToggles(body, elementsPane, propertiesScroll);
        var propertiesTitle = Label("Window", new Thickness(0, 0, 0, 6));
        propertiesTitle.FontWeight = FontWeights.SemiBold;
        _properties.Children.Add(propertiesTitle);
        Field("Caption", _title);
        AddDimensionFields();
        UpdateResizeChoices();
        _resize.SelectionChanged += (_, _) => Changed();
        Field("Resize", _resize);
        _properties.Children.Add(_eventTools);
        return root;

        Button Add(Panel panel, string key, Func<Task> execute, bool entry = false)
        {
            var button = new Button();
            Configure(button, key);
            button.Click += async (_, _) =>
            {
                try { await execute(); }
                catch (Exception ex) { ShowError(ex); }
            };
            (entry ? _entryCommands : _projectCommands).Add(button);
            panel.Children.Add(button);
            return button;
        }
    }

    private void Configure(Button button, string key)
    {
        button.Padding = new Thickness(9, 4, 9, 4);
        button.MinHeight = 32;
        button.Margin = new Thickness(0, 0, 5, 4);
        AutomationProperties.SetAutomationId(button, "VisualConstructor." + key);
        SetCommandLabel(button, key);
    }

    private void SetCommandLabel(Button button, string key)
    {
        var label = T(key);
        AutomationProperties.SetName(button, label);
        var glyph = key switch
        {
            "New" => "\uE7C3", "Open" => "\uE838", "Save" => "\uE74E", "SaveAs" => "\uE792", "Folder" => "\uE8B7",
            "Cancel" => "\uE71A", "AddForm" or "AddElement" => "\uE710", "RemoveForm" => "\uE738", "RemoveElement" => "\uE74D",
            "EditStep" => "\uE70F", "RemoveStep" => "\uE74D", "MoveUp" => "\uE74A", "MoveDown" => "\uE74B",
            "UndoSuggestion" => "\uE7A7", "PrepareGemma" => "\uE896", "GemmaHistory" => "\uE81C",
            "PreviewSetSize" => "\uE740", "PreviewApplySize" => "\uE73E",
            _ => null
        };
        if (glyph is null)
        {
            button.Content = key == "CheckBuild" ? T("CheckBuildShort") : label;
            if (key == "CheckBuild") button.ToolTip = label;
            return;
        }
        button.MinWidth = 34;
        button.Width = 34;
        button.Padding = new Thickness(0);
        button.ToolTip = label;
        ToolTipService.SetShowOnDisabled(button, true);
        button.Content = new TextBlock
        {
            Text = glyph, FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 15
        };
    }

    private void RefreshRecent()
    {
        var loading = _loading;
        _loading = true;
        try
        {
            var paths = _recentPaths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var entries = paths.GroupBy(Path.GetFileNameWithoutExtension, StringComparer.CurrentCultureIgnoreCase)
                .SelectMany(group => group.Select(path => new Choice(path, group.Count() == 1 ? group.Key!
                    : $"{group.Key} · {Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(path)))}")))
                .ToDictionary(choice => choice.Value, StringComparer.OrdinalIgnoreCase);
            _recent.ItemsSource = new[] { new Choice("", T("RecentShort")) }.Concat(paths.Select(path => entries[path])).ToArray();
            _recent.SelectedIndex = 0;
        }
        finally { _loading = loading; }
    }

    private void Field(string key, Control input)
    {
        AddFieldLabel(key, input);
        _properties.Children.Add(input);
    }

    private TextBlock AddFieldLabel(string key, Control input)
    {
        var label = Label(key, new Thickness(0, 4, 0, 2));
        AutomationProperties.SetLabeledBy(input, label);
        AutomationProperties.SetAutomationId(input, "VisualConstructor." + key);
        _properties.Children.Add(label);
        if (input is TextBox box)
        {
            box.MinHeight = 29;
            box.Padding = new Thickness(6, 3, 6, 3);
            box.SetResourceReference(TextBox.CaretBrushProperty, "TextPrimaryBrush");
            box.TextChanged += (_, _) => Changed();
        }
        return label;
    }

    private TextBlock Label(string key, Thickness margin)
    {
        var label = new TextBlock { Text = T(key), Margin = margin, TextWrapping = TextWrapping.Wrap };
        _labels.Add((label, key));
        return label;
    }

    internal void Configure(Func<string, string> localize)
    {
        _localize = localize;
        foreach (var (label, key) in _labels) label.Text = T(key);
        foreach (var button in _entryCommands.Concat(_projectCommands).Append(_cancel).Append(_back))
            SetCommandLabel(button, AutomationProperties.GetAutomationId(button)["VisualConstructor.".Length..]);
        AutomationProperties.SetName(_recent, T("Recent"));
        RefreshRecent();
        if (_project is null) _projectPath.Text = T("StartHint");
        UpdateResizeChoices();
        ConfigurePreviewLabels();
        ConfigureFormLabels();
        ConfigureElementLabels();
        SetCommandLabel(_toggleEditorElements, "ToggleEditorElements");
        SetCommandLabel(_toggleEditorProperties, "ToggleEditorProperties");
    }

    private void UpdateResizeChoices()
    {
        var current = _resize.SelectedValue;
        var loading = _loading;
        _loading = true;
        try
        {
            _resize.ItemsSource = new[] { "CanResize", "CanResizeWithGrip", "CanMinimize", "NoResize" }
                .Select(value => new Choice(value, T(value))).ToArray();
            _resize.SelectedValue = current;
        }
        finally { _loading = loading; }
    }

    private void Changed()
    {
        if (_loading || _project is null) return;
        _dirty = true;
        _state.Text = T("Unsaved");
        SyncDimensionSliders();
        UpdateCanvas();
    }
}
