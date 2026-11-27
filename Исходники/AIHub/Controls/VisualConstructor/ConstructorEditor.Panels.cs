using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;

namespace AIHub.Controls.VisualConstructor;

internal sealed partial class ConstructorEditor
{
    private readonly Button _toggleEditorElements = new();
    private readonly Button _toggleEditorProperties = new();

    private void AddPanelToggles(Grid body, FrameworkElement elements, FrameworkElement properties)
    {
        bool? elementsOpen = null;
        bool? propertiesOpen = null;
        Install(_toggleEditorElements, "ToggleEditorElements", () =>
        {
            elementsOpen = elements.Visibility != Visibility.Visible;
            if (body.ActualWidth < 800 && elementsOpen == true) propertiesOpen = false;
            Refresh();
        });
        Install(_toggleEditorProperties, "ToggleEditorProperties", () =>
        {
            propertiesOpen = properties.Visibility != Visibility.Visible;
            if (body.ActualWidth < 800 && propertiesOpen == true) elementsOpen = false;
            Refresh();
        });
        body.SizeChanged += (_, _) => Refresh();

        void Install(Button button, string key, Action change)
        {
            Configure(button, key);
            DockPanel.SetDock(button, Dock.Right);
            _formTools.Children.Insert(0, button);
            button.Click += (_, _) => change();
        }

        void Refresh()
        {
            var wide = body.ActualWidth >= 800;
            // In a narrow editor one inspector can be open while leaving room for the preview.
            var showElements = elementsOpen ?? wide;
            var showProperties = propertiesOpen ?? wide;
            if (!wide && showElements && showProperties) showProperties = false;
            Apply(elements, body.ColumnDefinitions[0], showElements, 204);
            Apply(properties, body.ColumnDefinitions[2], showProperties, 216);
        }
    }

    private static void Apply(FrameworkElement item, ColumnDefinition column, bool visible, double width)
    {
        item.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        column.Width = new GridLength(visible ? width : 0);
    }
}
