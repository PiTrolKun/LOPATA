using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using AIHub.Services;
using AIHub.Services.ImageLayers;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace AIHub.Controls.ImageLayers;

internal sealed partial class ImageLayersControl
{
    private Button _lassoTool = null!, _sceneReview = null!, _completeFrame = null!, _sendToEditor = null!;

    private Button Command(string key, Action action, bool primary = false)
    {
        var button = MusicWishUi.Button(T(key), "ImageLayers." + key, action);
        button.MinHeight = 32;
        button.Padding = new(10, 3, 10, 3);
        button.Margin = new(0, 0, 6, 5);
        button.SetResourceReference(StyleProperty, primary ? "SettingsPrimaryActionStyle" : "SettingsActionButtonStyle");
        var text = (TextBlock)button.Content;
        text.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(Button.Foreground)) { Source = button });
        return button;
    }

    private void BuildInterface()
    {
        _open = Command("Open", () => _ = OpenAsync());
        _save = Command("Save", () => _ = RunAsync(token => SaveAsync(false, token)));
        _saveAs = Command("SaveAs", () => _ = RunAsync(token => SaveAsync(true, token)));
        _analyze = ToolCommand("Analyze", () => _ = AnalyzeAsync(false), primary: true);
        _refine = ToolCommand("Refine", () => _ = AnalyzeAsync(true), "RefineHelp");
        _sceneReview = ToolCommand("ReviewScene", () => _ = RunSceneAsync(LayerAdditionalMethod.SceneReview), "ReviewSceneHelp");
        _completeFrame = ToolCommand("CompleteFrame", () => _ = RunSceneAsync(LayerAdditionalMethod.CompleteFrame), "CompleteFrameHelp");
        var extras = BuildAdditionalMenu();
        _cancel = ToolCommand("Cancel", () => _operation?.Cancel());
        _handTool = ToolCommand("Hand", () => SetTool(LayerCanvasTool.Hand), "HandHelp");
        _regionTool = ToolCommand("RegionTool", () => SetTool(LayerCanvasTool.Region), "RegionToolHelp");
        _lassoTool = ToolCommand("Lasso", ChooseLasso, "LassoHelp");
        _pointTool = ToolCommand("Points", () => SetTool(LayerCanvasTool.Points), "PointsHelp");
        _pencilTool = ToolCommand("Pencil", () => SetTool(LayerCanvasTool.Pencil), "PencilHelp");
        _edgeDetails = ToolCommand("EdgeDetails", () => _ = RefineEdgesAsync(), "EdgeDetailsHelp");
        _sendToEditor = ToolCommand("SendToEditor", ShowEditorPlaceholder, "EditorPlaceholder");
        _reload = Command("Restore", () => _ = RestoreAsync());
        Choice(_scope, "Scope", new[] { "FullFrame", "Region", "SelectedLayer" }.Select(T), 0);
        Choice(_depth, "Depth", new[] { "Objects", "Parts", "Details" }.Select(T), 0);
        Choice(_spacing, "Step", LayerPointLayout.Steps.Select(value => string.Format(T("Pixels"), value)), 3);
        Choice(_view, "View", new[] { "Original", "Light", "Dark" }.Select(T), 0, selectableItems: true);

        var files = Row(_open, _save, _saveAs);
        var controls = Row(Labeled("Scope", _scope), Labeled("Depth", _depth), _analyze, _refine,
            _sceneReview, _completeFrame, extras, _cancel,
            _handTool, _regionTool, _lassoTool, _pointTool, _pencilTool, _edgeDetails, _sendToEditor,
            Labeled("Step", _spacing), Labeled("View", _view));
        var projectBar = new DockPanel();
        _caption.Margin = new(8, 0, 0, 5);
        DockPanel.SetDock(files, Dock.Left);
        projectBar.Children.Add(files);
        projectBar.Children.Add(_caption);
        var header = new StackPanel { Margin = new(16, 12, 16, 5) };
        header.Children.Add(projectBar);
        header.Children.Add(controls);
        header.Children.Add(BuildLassoBar());

        _tree.MinHeight = 90;
        _tree.BorderThickness = new(0);
        _tree.FontSize = 14;
        _history.DisplayMemberPath = nameof(HistoryRow.Label);
        _history.BorderThickness = new(0);
        _history.FontSize = 13;
        AutomationProperties.SetAutomationId(_canvas, "ImageLayers.Canvas");
        AutomationProperties.SetAutomationId(_tree, "ImageLayers.Tree");
        AutomationProperties.SetHelpText(_tree, T("FocusLayerHelp"));
        AutomationProperties.SetAutomationId(_history, "ImageLayers.History");
        _welcome.Text = T("OpenHint");
        _welcome.MaxWidth = 360;
        _welcome.FontSize = 18;
        _welcome.HorizontalAlignment = HorizontalAlignment.Center;
        _welcome.VerticalAlignment = System.Windows.VerticalAlignment.Center;
        _welcome.IsHitTestVisible = false;
        _welcome.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        var canvas = new Grid();
        canvas.Children.Add(_canvas);
        canvas.Children.Add(_welcome);

        var sidebar = new Grid { Margin = new(10, 0, 0, 0) };
        sidebar.RowDefinitions.Add(new() { Height = new(3, GridUnitType.Star), MinHeight = 125 });
        sidebar.RowDefinitions.Add(new() { Height = new(6) });
        sidebar.RowDefinitions.Add(new() { Height = new(2, GridUnitType.Star), MinHeight = 140 });
        var layerPanel = Panel(Heading(T("Layers")), _tree);
        sidebar.Children.Add(layerPanel);
        var split = new GridSplitter { Height = 5, HorizontalAlignment = HorizontalAlignment.Stretch,
            ResizeDirection = GridResizeDirection.Rows, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        split.SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        Grid.SetRow(split, 1);
        sidebar.Children.Add(split);
        _historyTitle.Text = T("History");
        _historyTitle.FontSize = 15;
        _historyTitle.FontWeight = FontWeights.SemiBold;
        _historyTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        _historyTitle.Margin = new(0, 0, 0, 9);
        var historyPanel = Panel(_historyTitle, _history, _reload);
        Grid.SetRow(historyPanel, 2);
        sidebar.Children.Add(historyPanel);

        var workspace = new Grid { Margin = new(16, 4, 16, 12) };
        workspace.ColumnDefinitions.Add(new());
        workspace.ColumnDefinitions.Add(new() { Width = new(5) });
        workspace.ColumnDefinitions.Add(new() { Width = new(306), MinWidth = 230, MaxWidth = 500 });
        workspace.Children.Add(canvas);
        var sideSplit = new GridSplitter { Width = 5, VerticalAlignment = VerticalAlignment.Stretch,
            ResizeDirection = GridResizeDirection.Columns, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        sideSplit.SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        Grid.SetColumn(sideSplit, 1);
        workspace.Children.Add(sideSplit);
        Grid.SetColumn(sidebar, 2);
        workspace.Children.Add(sidebar);

        var zoom = Row(Command("ZoomOut", () => _canvas.Zoom(1 / 1.2)), _zoom,
            Command("ZoomIn", () => _canvas.Zoom(1.2)), Command("Fit", _canvas.Fit));
        _zoom.VerticalAlignment = VerticalAlignment.Center;
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        AutomationProperties.SetAutomationId(_status, "ImageLayers.Status");
        var footer = new DockPanel { Margin = new(16, 3, 10, 4) };
        DockPanel.SetDock(zoom, Dock.Right);
        footer.Children.Add(zoom); footer.Children.Add(_status);

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(_progress, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(header); root.Children.Add(_progress); root.Children.Add(footer); root.Children.Add(workspace);
        Content = root;
    }

    private void Choice(ComboBox control, string key, IEnumerable<string> options, int selected, bool selectableItems = false)
    {
        foreach (var option in options) control.Items.Add(selectableItems ? new ComboBoxItem { Content = option } : option);
        control.SelectedIndex = selected;
        control.MinWidth = 62;
        control.MinHeight = 32;
        control.Margin = new(0, 0, 8, 5);
        control.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(control, T(key));
        AutomationProperties.SetAutomationId(control, "ImageLayers." + key);
    }

    private StackPanel Labeled(string key, ComboBox control)
    {
        var label = new TextBlock { Text = T(key), Margin = new(0, 0, 5, 5), VerticalAlignment = VerticalAlignment.Center };
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(label); panel.Children.Add(control);
        return panel;
    }

    private static WrapPanel Row(params UIElement[] children)
    {
        var row = new WrapPanel();
        foreach (var child in children) row.Children.Add(child);
        return row;
    }

    private static TextBlock Heading(string value) => new()
    { Text = value, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 9) };

    private static Border Panel(UIElement heading, UIElement list, UIElement? footer = null)
    {
        var dock = new DockPanel();
        DockPanel.SetDock(heading, Dock.Top);
        dock.Children.Add(heading);
        if (footer is not null) { DockPanel.SetDock(footer, Dock.Bottom); dock.Children.Add(footer); }
        dock.Children.Add(list);
        var border = new Border { CornerRadius = new(7), BorderThickness = new(1), Padding = new(12), Child = dock };
        border.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        return border;
    }

    private void SetTool(LayerCanvasTool tool)
    {
        _canvas.Tool = tool;
        if (tool != LayerCanvasTool.Hand) _view.SelectedIndex = 0;
        if (tool == LayerCanvasTool.Region) _scope.SelectedIndex = 1;
        else if (tool == LayerCanvasTool.Lasso)
        {
            _scope.SelectedIndex = 0;
            SetStatus(T("LassoHelp"));
        }
        RefreshTools();
        if (tool == LayerCanvasTool.Lasso) _canvas.Focus();
    }

    private void RefreshTools()
    {
        var busy = _operation is not null;
        var hasProject = _document is not null;
        var hasLayer = _selectedMask?.Any == true;
        _open.IsEnabled = !busy;
        _handTool.IsEnabled = hasProject;
        _save.IsEnabled = _saveAs.IsEnabled = _scope.IsEnabled = _depth.IsEnabled = _regionTool.IsEnabled = hasProject && !busy;
        _lassoTool.IsEnabled = _completeFrame.IsEnabled = _sendToEditor.IsEnabled = hasProject && !busy;
        _sceneReview.IsEnabled = hasProject && !busy && _document!.Layers.Values.Any(layer => layer.Mask is not null);
        _pointTool.IsEnabled = _pencilTool.IsEnabled = _spacing.IsEnabled = hasLayer && !busy;
        _view.IsEnabled = hasProject && !busy;
        foreach (var option in _view.Items.OfType<ComboBoxItem>().Skip(1))
            option.IsEnabled = hasLayer;
        if (!hasLayer && _view.SelectedIndex > 0) _view.SelectedIndex = 0;
        _cancel.IsEnabled = busy;
        _cancel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _progress.IsIndeterminate = busy;
        _progress.Visibility = busy ? Visibility.Visible : Visibility.Hidden;
        _tree.IsEnabled = _history.IsEnabled = !busy;
        _reload.IsEnabled = !busy && _history.SelectedItem is HistoryRow { Current: false };
        _analyze.IsEnabled = hasProject && !busy && (_scope.SelectedIndex switch
        { 1 => _region is not null, 2 => hasLayer, _ => true });
        _refine.IsEnabled = !busy && hasLayer && _selected is Guid id && _document?.RefinableVersion(id) is not null;
        _welcome.Visibility = hasProject ? Visibility.Collapsed : Visibility.Visible;
        _canvas.ReadOnly = busy;
        _canvas.ShowRegion(_scope.SelectedIndex == 1 ? _region : null);
        var points = _selected is Guid selected ? _document?.Layers.GetValueOrDefault(selected)?.Points : null;
        var matte = _selected is Guid matteLayer && hasLayer ? _document?.ActiveMatte(matteLayer) : null;
        RefreshAdditionalTools(hasProject && !busy, hasLayer, matte is not null);
        _canvas.ShowLayer(_selected, _selectedMask, points, LayerPointLayout.Steps[Math.Max(0, _spacing.SelectedIndex)],
            hasLayer ? (LayerCanvasView)Math.Max(0, _view.SelectedIndex) : LayerCanvasView.Original, matte);
        foreach (var (button, tool) in new[] { (_handTool, LayerCanvasTool.Hand), (_regionTool, LayerCanvasTool.Region),
            (_lassoTool, LayerCanvasTool.Lasso), (_pointTool, LayerCanvasTool.Points), (_pencilTool, LayerCanvasTool.Pencil) })
            button.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty,
                button.IsEnabled && _canvas.Tool == tool ? "AccentBrush" : "LineBrush");
        RefreshLassoBar();
    }
}
