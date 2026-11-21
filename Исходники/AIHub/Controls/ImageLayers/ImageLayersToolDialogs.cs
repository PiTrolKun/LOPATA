using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services.ImageLayers;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using RadioButton = System.Windows.Controls.RadioButton;
using PixelRect = System.Drawing.Rectangle;

namespace AIHub.Controls.ImageLayers;

internal static partial class ImageLayersToolDialogs
{
    internal static Window Description(Window owner, Func<string, string> t, out Func<string> value) =>
        Prompt(owner, t, "FindByDescription", "DescribeHelp", "FindAction", "DescriptionInput", 140, out value);

    internal static Window LayerName(Window owner, Func<string, string> t, out Func<string> value) =>
        Prompt(owner, t, "CreateManualLayer", "ManualNameHelp", "CreateLayerAction", "ManualLayerName", 160, out value);

    private static Window Prompt(Window owner, Func<string, string> t, string title, string help, string accept,
        string automationId, int maxLength, out Func<string> value)
    {
        var dialog = Create(owner, t(title), 500, 225);
        dialog.ResizeMode = ResizeMode.NoResize;
        var input = new TextBox { MaxLength = maxLength, MinHeight = 32, VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(input, t(title));
        AutomationProperties.SetAutomationId(input, "ImageLayers." + automationId);
        var action = Accept(dialog, t, accept);
        action.IsEnabled = false;
        input.TextChanged += (_, _) => action.IsEnabled = !string.IsNullOrWhiteSpace(input.Text) && !input.Text.Any(char.IsControl);
        dialog.Loaded += (_, _) => input.Focus();
        var root = new StackPanel { Margin = new(20) };
        root.Children.Add(Help(t(help)));
        root.Children.Add(input);
        root.Children.Add(Actions(action, Cancel(dialog, t)));
        dialog.Content = root;
        value = () => input.Text.Trim();
        return dialog;
    }

    internal static Window Markers(Window owner, Func<string, string> t, LayerSource source, PixelRect area,
        out Func<LayerMarker[]> values)
    {
        var dialog = Create(owner, t("SelectByMarkers"), 940, 690);
        var canvas = new LayerCanvas { Tool = LayerCanvasTool.Markers };
        canvas.ShowSource(source, area);
        AutomationProperties.SetAutomationId(canvas, "ImageLayers.MarkerCanvas");
        var points = new List<LayerMarker>();
        var foreground = new RadioButton { Content = t("ObjectMarker"), IsChecked = true, Margin = new(6) };
        var background = new RadioButton { Content = t("BackgroundMarker"), Margin = new(6) };
        var apply = Accept(dialog, t, "Analyze");
        apply.IsEnabled = false;
        void Refresh()
        {
            canvas.ShowMarkers([.. points]);
            apply.IsEnabled = points.Any(point => point.Foreground) && points.Any(point => !point.Foreground);
        }
        canvas.MarkerPlaced += point =>
        {
            points.RemoveAll(existing => existing.X == point.X && existing.Y == point.Y);
            points.Add(point with { Foreground = foreground.IsChecked == true });
            Refresh();
        };
        var clear = Action(t, "ClearMarkers", () => { points.Clear(); Refresh(); });
        var undo = Action(t, "UndoMarker", () => { if (points.Count > 0) points.RemoveAt(points.Count - 1); Refresh(); });
        var footer = Actions(foreground, background, undo, clear, apply, Cancel(dialog, t));
        var root = new DockPanel { Margin = new(16) };
        var help = Help(t("MarkersHelp"));
        DockPanel.SetDock(help, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(help);
        root.Children.Add(footer);
        root.Children.Add(canvas);
        dialog.Content = root;
        values = () => [.. points];
        return dialog;
    }

    internal static Window CutoutReview(Window owner, Func<string, string> t, LayerDocument before, LayerDocument after,
        Guid selected, PixelRect area, Func<Window, Button>? extraAction = null)
    {
        var dialog = Create(owner, t("Comparison"), 1140, 700);
        var first = new LayerCanvas { Tool = LayerCanvasTool.Hand, ReadOnly = true };
        var second = new LayerCanvas { Tool = LayerCanvasTool.Hand, ReadOnly = true };
        first.ShowSource(before.Source, area);
        second.ShowSource(after.Source, area);
        first.LinkViewport(second);
        var oldMask = before.Aggregate(selected);
        var newMask = after.Aggregate(selected);
        void OnBackground(LayerCanvasView view)
        {
            first.ShowLayer(selected, oldMask, null, 8, view, before.ActiveMatte(selected));
            second.ShowLayer(selected, newMask, null, 8, view, after.ActiveMatte(selected));
        }
        OnBackground(LayerCanvasView.Light);
        var controls = new List<UIElement>
        {
            Action(t, "Light", () => OnBackground(LayerCanvasView.Light)),
            Action(t, "Dark", () => OnBackground(LayerCanvasView.Dark))
        };
        if (extraAction is not null) controls.Add(extraAction(dialog));
        controls.Add(Accept(dialog, t, extraAction is not null ? "ApplyEdgeDetails" : "ApplyAll"));
        controls.Add(Cancel(dialog, t));
        var footer = Actions([.. controls]);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new());
        grid.ColumnDefinitions.Add(new() { Width = new(12) });
        grid.ColumnDefinitions.Add(new());
        var panes = new[] { Pane(t("Before"), first), Pane(t("After"), second) };
        Grid.SetColumn(panes[1], 2);
        foreach (var pane in panes) grid.Children.Add(pane);
        var root = new DockPanel { Margin = new(16) };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(grid);
        dialog.Content = root;
        return dialog;
    }

    internal static Window EdgeReview(Window owner, Func<string, string> t, LayerDocument before, LayerDocument after,
        Guid layer, PixelRect area, LayerEdgeStroke[] strokes, out Func<LayerEdgeStroke[]?> revision)
    {
        LayerEdgeStroke[]? next = null;
        var window = CutoutReview(owner, t, before, after, layer, area, review => Action(t, "EdgeHintBrush", () =>
        {
            var editor = EdgeGuides(review, t, before.Source, area, strokes, out var hints);
            if (editor.ShowDialog() != true) return;
            next = hints();
            review.DialogResult = true;
        }));
        revision = () => next;
        return window;
    }

    private static Window EdgeGuides(Window owner, Func<string, string> t, LayerSource source, PixelRect area,
        LayerEdgeStroke[] existing, out Func<LayerEdgeStroke[]> values)
    {
        var dialog = Create(owner, t("EdgeHintBrush"), 1000, 700);
        var canvas = new LayerCanvas { Tool = LayerCanvasTool.EdgeBrush };
        canvas.ShowSource(source, area);
        AutomationProperties.SetAutomationId(canvas, "ImageLayers.EdgeGuideCanvas");
        AutomationProperties.SetName(canvas, t("EdgeHintBrush"));
        var strokes = existing.ToList();
        var retain = new RadioButton { Content = t("EdgeKeepBrush"), IsChecked = true, Margin = new(6) };
        var omit = new RadioButton { Content = t("EdgeRemoveBrush"), Margin = new(6) };
        AutomationProperties.SetAutomationId(retain, "ImageLayers.EdgeKeepBrush");
        AutomationProperties.SetAutomationId(omit, "ImageLayers.EdgeRemoveBrush");
        var recalculate = Accept(dialog, t, "EdgeRecalculate");
        var undo = Action(t, "EdgeUndoStroke", () => strokes.RemoveAt(strokes.Count - 1));
        var clear = Action(t, "EdgeClearStrokes", strokes.Clear);
        void Refresh()
        {
            canvas.ShowEdgeGuides([.. strokes]);
            undo.IsEnabled = clear.IsEnabled = strokes.Count > 0;
            recalculate.IsEnabled = !strokes.SequenceEqual(existing);
        }
        undo.Click += (_, _) => Refresh();
        clear.Click += (_, _) => Refresh();
        retain.Checked += (_, _) => canvas.KeepEdgeGuide = true;
        omit.Checked += (_, _) => canvas.KeepEdgeGuide = false;
        canvas.EdgeGuideDrawn += stroke => { strokes.Add(stroke); Refresh(); };
        Refresh();
        var footer = Actions(retain, omit, undo, clear, recalculate, Cancel(dialog, t));
        var root = new DockPanel { Margin = new(16) };
        var help = Help(t("EdgeBrushHelp"));
        DockPanel.SetDock(help, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(help);
        root.Children.Add(footer);
        root.Children.Add(canvas);
        dialog.Content = root;
        values = () => [.. strokes];
        return dialog;
    }

    private static DockPanel Pane(string text, LayerCanvas canvas)
    {
        var pane = new DockPanel();
        var title = Help(text);
        DockPanel.SetDock(title, Dock.Top);
        pane.Children.Add(title);
        pane.Children.Add(canvas);
        return pane;
    }

    private static Window Create(Window owner, string title, int width, int height)
    {
        var dialog = new Window
        {
            Owner = owner, Title = title, Width = width, Height = height, MinWidth = 460, MinHeight = 210,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false
        };
        dialog.Resources.MergedDictionaries.Add(owner.Resources);
        dialog.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri("/AIHub;component/Controls/SettingsResources.xaml", UriKind.Relative) });
        dialog.SetResourceReference(Window.BackgroundProperty, "WindowBackgroundBrush");
        dialog.SetResourceReference(Window.ForegroundProperty, "TextPrimaryBrush");
        return dialog;
    }

    private static TextBlock Help(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 10) };

    private static WrapPanel Actions(params UIElement[] controls)
    {
        var row = new WrapPanel { Margin = new(0, 12, 0, 0) };
        foreach (var control in controls) row.Children.Add(control);
        return row;
    }

    private static Button Accept(Window dialog, Func<string, string> t, string key)
    {
        var button = Action(t, key, () => dialog.DialogResult = true);
        button.IsDefault = true;
        return button;
    }

    private static Button Cancel(Window dialog, Func<string, string> t, string key = "KeepCurrent")
    {
        var button = Action(t, key, () => dialog.DialogResult = false);
        button.IsCancel = true;
        return button;
    }

    private static Button Action(Func<string, string> t, string key, Action action) =>
        MusicWishUi.Button(t(key), "ImageLayers." + key, action);
}
