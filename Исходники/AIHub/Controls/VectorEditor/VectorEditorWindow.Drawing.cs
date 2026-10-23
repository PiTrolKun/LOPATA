using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;
using Orientation = System.Windows.Controls.Orientation;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using ColorConverter = System.Windows.Media.ColorConverter;
using Control = System.Windows.Controls.Control;

namespace AIHub.Controls.VectorEditor;

internal sealed partial class VectorEditorWindow
{
    private string _drawingFill = "#3A9FF9", _drawingStroke = "#173448";
    private bool _drawingNoFill, _drawingNoStroke;
    private double _drawingStrokeWidth = 2;

    private UIElement DrawingPalette()
    {
        var panel = new StackPanel();
        panel.Children.Add(Caption(T("Drawing.Title"), true));
        panel.Children.Add(Caption(T("Drawing.Hint")));
        PaintRow("fill", () => _drawingFill, v => _drawingFill = v,
            () => _drawingNoFill, v => _drawingNoFill = v);
        PaintRow("stroke", () => _drawingStroke, v => _drawingStroke = v,
            () => _drawingNoStroke, v => _drawingNoStroke = v);

        var widthRow = new Grid { Margin = new(4, 3, 4, 8) };
        widthRow.ColumnDefinitions.Add(new() { Width = new GridLength(72) });
        widthRow.ColumnDefinitions.Add(new());
        widthRow.Children.Add(Caption(T("Drawing.Width")));
        var width = new TextBox { Text = _drawingStrokeWidth.ToString(CultureInfo.InvariantCulture), Padding = new(6, 4, 6, 4) };
        AutomationProperties.SetAutomationId(width, "Vector.Editor.Drawing.Width");
        AutomationProperties.SetName(width, T("Property.strokeWidth"));
        Grid.SetColumn(width, 1); widthRow.Children.Add(width); panel.Children.Add(widthRow);
        async Task ApplyWidth()
        {
            var value = Numeric(width.Text.Trim());
            if (value < 0) throw new System.IO.InvalidDataException("Vector.Editor.Error.Number");
            _drawingStrokeWidth = value;
            width.Text = value.ToString(CultureInfo.InvariantCulture);
            await UpdateDrawing();
        }
        width.PreviewKeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await Safe(ApplyWidth); } };
        width.LostKeyboardFocus += async (_, _) => await Safe(ApplyWidth);
        return panel;

        void PaintRow(string key, Func<string> color, Action<string> setColor, Func<bool> none, Action<bool> setNone)
        {
            var row = new Grid { Margin = new(4, 3, 4, 3) };
            row.ColumnDefinitions.Add(new() { Width = new GridLength(64) });
            row.ColumnDefinitions.Add(new());
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.Children.Add(Caption(T("Property." + key)));
            var swatch = new Border { Width = 16, Height = 18, BorderThickness = new(1), Margin = new(0, 0, 6, 0) };
            swatch.SetResourceReference(Border.BorderBrushProperty, "TextSecondaryBrush");
            var hex = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            hex.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(swatch); content.Children.Add(hex);
            var choose = new Button { Content = content, Padding = new(5, 5, 5, 5), Margin = new(0, 0, 3, 0), ToolTip = T("ChooseColor") };
            AutomationProperties.SetAutomationId(choose, "Vector.Editor.Drawing." + key);
            Grid.SetColumn(choose, 1); row.Children.Add(choose);
            var empty = new CheckBox { Style = new Style(typeof(CheckBox)), Content = T("Drawing.None"), IsChecked = none(), FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
                ToolTip = T("Drawing.No." + key), Margin = new(2, 0, 0, 0) };
            empty.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
            AutomationProperties.SetAutomationId(empty, "Vector.Editor.Drawing.No." + key);
            AutomationProperties.SetName(empty, T("Drawing.No." + key));
            Grid.SetColumn(empty, 2); row.Children.Add(empty); panel.Children.Add(row);
            void Refresh()
            {
                swatch.Background = none() ? Brushes.Transparent : new SolidColorBrush((Color)ColorConverter.ConvertFromString(color()));
                hex.Text = none() ? "∅" : color();
                AutomationProperties.SetName(choose, T("Property." + key) + ": " + (none() ? T("Drawing.None") : color()));
            }
            empty.Click += async (_, _) => await Safe(async () => { setNone(empty.IsChecked == true); Refresh(); await UpdateDrawing(); });
            choose.Click += async (_, _) => await Safe(async () =>
            {
                using var picker = new System.Windows.Forms.ColorDialog { FullOpen = true, AnyColor = true, Color = System.Drawing.ColorTranslator.FromHtml(color()) };
                if (picker.ShowDialog(new DrawingColorOwner(new WindowInteropHelper(this).Handle)) != System.Windows.Forms.DialogResult.OK) return;
                setColor($"#{picker.Color.R:X2}{picker.Color.G:X2}{picker.Color.B:X2}");
                setNone(false); empty.IsChecked = false; Refresh(); await UpdateDrawing();
            });
            Refresh();
        }
    }

    private Task UpdateDrawing() => _browser.CallAsync("drawing", new
    {
        fill = _drawingNoFill ? "none" : _drawingFill,
        stroke = _drawingNoStroke ? "none" : _drawingStroke,
        width = _drawingStrokeWidth
    });

    private sealed record DrawingColorOwner(IntPtr Handle) : System.Windows.Forms.IWin32Window;
}
