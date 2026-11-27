using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Xml.Linq;
using AIHub.Services.VisualConstructor;
using W = System.Windows.Controls;

namespace AIHub.Controls.VisualConstructor;

internal sealed partial class ConstructorEditor
{
    private readonly Dictionary<string, FrameworkElement> _previewElements = [];
    private PreviewSelection? _previewSelection;
    private AdornerLayer? _previewSelectionLayer;

    private void RenderDesign()
    {
        ClearPreviewSelection();
        _previewElements.Clear();
        _sampleBody.Child = _design?.Surface is { } source ? RenderElement(source) : null;
        ShowPreviewSelection();
    }

    private void SelectFromPreview(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        string? selected = null;
        for (var current = e.OriginalSource as DependencyObject;
             current is not null && current != _sampleBody;
             current = current is Visual or Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            if (current is W.Primitives.ScrollBar) return;
            if (current is FrameworkElement { Tag: string name } && _previewElements.ContainsKey(name))
            {
                selected = name;
                break;
            }
        }
        _elementList.SelectedValue = selected;
        e.Handled = true;
    }

    private void ClearPreviewSelection()
    {
        if (_previewSelection is null) return;
        _previewSelectionLayer!.Remove(_previewSelection);
        _previewSelection = null;
        _previewSelectionLayer = null;
    }

    private void ShowPreviewSelection()
    {
        ClearPreviewSelection();
        RevealSelectedPreviewTab();
        if (ElementId is not { } id || !_previewElements.TryGetValue(id, out var visual)) return;
        _previewSelectionLayer = AdornerLayer.GetAdornerLayer(visual) ?? _sampleDecoration.AdornerLayer;
        _previewSelection = new PreviewSelection(visual) { IsHitTestVisible = false };
        _previewSelectionLayer.Add(_previewSelection);
    }

    private void RevealSelectedPreviewTab()
    {
        if (_element is null) return;
        foreach (var page in _element.AncestorsAndSelf(ConstructorProject.Wpf + "TabItem").Reverse())
            if (_previewElements.TryGetValue(ConstructorLayout.Name(page), out var preview) && preview is W.TabItem selected)
                selected.IsSelected = true;
    }

    private sealed class PreviewSelection(FrameworkElement selected) : Adorner(selected)
    {
        protected override void OnRender(DrawingContext drawingContext)
        {
            var width = selected.ActualWidth;
            var height = selected.ActualHeight;
            if (width <= 4 || height <= 4) return;
            var brush = TryFindResource("AccentBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.RoyalBlue;
            drawingContext.DrawRoundedRectangle(null, new System.Windows.Media.Pen(brush, 4),
                new Rect(2, 2, width - 4, height - 4), 2, 2);
        }
    }

    private FrameworkElement? RenderElement(XElement node)
    {
        if (node.Name.Namespace != ConstructorProject.Wpf) return null;
        string Attribute(string key) => ConstructorProject.ReadLiteral((string?)node.Attribute(key) ?? "");
        var content = Attribute("Content");
        var title = Attribute("Header");
        FrameworkElement? element = node.Name.LocalName switch
        {
            "DockPanel" => new W.DockPanel(),
            "ScrollViewer" => new W.ScrollViewer
            {
                Content = Children(node).FirstOrDefault(), VerticalScrollBarVisibility = W.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = W.ScrollBarVisibility.Disabled
            },
            "StackPanel" => new W.StackPanel(),
            "WrapPanel" => new W.WrapPanel(),
            "UniformGrid" => new W.Primitives.UniformGrid { Columns = int.TryParse(Attribute("Columns"), out var columns) ? Math.Max(1, columns) : 2 },
            "TextBlock" => new W.TextBlock { Text = Attribute("Text"), TextWrapping = TextWrapping.Wrap },
            "TextBox" => new W.TextBox { Text = Attribute("Text") },
            "Button" => new W.Button { Content = content, MinHeight = 30 },
            "CheckBox" => new W.CheckBox { Content = content, IsChecked = Attribute("IsChecked") == "True" },
            "ComboBox" => new W.ComboBox(),
            "ComboBoxItem" => new W.ComboBoxItem { Content = content },
            "Menu" => new W.Menu(),
            "MenuItem" => new W.MenuItem { Header = title },
            "StatusBar" => new W.Primitives.StatusBar(),
            "StatusBarItem" => new W.Primitives.StatusBarItem { Content = content },
            "GroupBox" => new W.GroupBox { Header = title, Content = Children(node).FirstOrDefault() },
            "TabControl" => new W.TabControl(),
            "TabItem" => new W.TabItem { Header = title, Content = Children(node).FirstOrDefault() },
            _ => null
        };
        if (element is null) return null;

        // Bypass the editor's implicit styles. The sample uses ordinary WPF controls.
        element.Style = null;
        element.Focusable = false;
        if (ConstructorLayout.IsElement(node))
        {
            var id = ConstructorLayout.Name(node);
            element.Tag = id;
            element.Cursor = System.Windows.Input.Cursors.Hand;
            _previewElements[id] = element;
            AutomationProperties.SetAutomationId(element, "VisualConstructor.PreviewElement." + id);
        }
        if (element is W.Panel panel)
            foreach (var child in Children(node)) panel.Children.Add(child);
        if (element is W.ItemsControl items)
        {
            foreach (var child in Children(node)) items.Items.Add(child);
            if (items is W.ComboBox combo && int.TryParse(Attribute("SelectedIndex"), out var selection))
                combo.SelectedIndex = selection;
        }
        if (Enum.TryParse<W.Dock>(Attribute("DockPanel.Dock"), out var dock)) W.DockPanel.SetDock(element, dock);
        if (double.TryParse(Attribute("MinWidth"), NumberStyles.Float, CultureInfo.InvariantCulture, out var minimum)
            && double.IsFinite(minimum) && minimum >= 0) element.MinWidth = minimum;
        var margin = Attribute("Margin").Split(',');
        var values = margin.Select(value => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var part) && double.IsFinite(part)
            ? part : 0).ToArray();
        if (values.Length == 1) element.Margin = new Thickness(values[0]);
        if (values.Length == 4) element.Margin = new Thickness(values[0], values[1], values[2], values[3]);
        return element;
    }

    private IEnumerable<FrameworkElement> Children(XElement node) => node.Elements().Select(RenderElement).OfType<FrameworkElement>();
}
