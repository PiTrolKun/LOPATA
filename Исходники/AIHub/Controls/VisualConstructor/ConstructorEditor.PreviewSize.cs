using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls.VisualConstructor;

internal sealed partial class ConstructorEditor
{
    private readonly ComboBox _previewSizeMode = NewChoiceBox();
    private readonly Button _applyPreviewSize = new();
    private readonly Button _setPreviewSize = new();
    private readonly TextBlock _previewSizeReadout = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 7, 0) };
    private readonly Thumb _previewGrip = new()
    {
        Width = 16, Height = 16, HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Bottom, Cursor = System.Windows.Input.Cursors.SizeNWSE, Focusable = false
    };
    private Size? _customPreviewSize;

    private Size PreviewDimensions() => (_previewSizeMode.SelectedValue as string) switch
    {
        "minimum" => new Size(_minWidthSlider.Value, _minHeightSlider.Value),
        "custom" when _customPreviewSize is { } size => ClampPreview(size.Width, size.Height),
        _ => new Size(_widthSlider.Value, _heightSlider.Value)
    };

    private Size ClampPreview(double width, double height) => new(
        Math.Clamp(width, _minWidthSlider.Value, MaximumDimension),
        Math.Clamp(height, _minHeightSlider.Value, MaximumDimension));

    private void AddPreviewSizeTools(WrapPanel bar, Grid sample, ScrollViewer viewport)
    {
        _previewSizeMode.Width = 132;
        _previewSizeMode.Margin = new Thickness(5, 0, 0, 3);
        AutomationProperties.SetAutomationId(_previewSizeMode, "VisualConstructor.PreviewSizeMode");
        _previewSizeMode.SelectionChanged += (_, _) => UpdateCanvas();
        Configure(_setPreviewSize, "PreviewSetSize");
        Configure(_applyPreviewSize, "PreviewApplySize");
        _setPreviewSize.Click += (_, _) => TryEdit(EditPreviewSize);
        _applyPreviewSize.Click += (_, _) =>
        {
            if (_operation is not null) return;
            var size = PreviewDimensions();
            _width.Text = size.Width.ToString(CultureInfo.CurrentCulture);
            _height.Text = size.Height.ToString(CultureInfo.CurrentCulture);
            _previewSizeMode.SelectedValue = "opening";
        };
        _setPreviewSize.SetBinding(IsEnabledProperty, new System.Windows.Data.Binding("IsEnabled") { Source = _properties });
        _previewGrip.SetBinding(IsEnabledProperty, new System.Windows.Data.Binding("IsEnabled") { Source = _properties });
        bar.Children.Add(_previewSizeMode);
        bar.Children.Add(_previewSizeReadout);
        bar.Children.Add(_setPreviewSize);
        bar.Children.Add(_applyPreviewSize);
        bar.SizeChanged += (_, _) => _previewSizeReadout.Visibility = bar.ActualWidth >= 530 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetAutomationId(_previewGrip, "VisualConstructor.PreviewResizeGrip");
        sample.Children.Add(_previewGrip);

        var start = new Point();
        var original = new Size();
        var scale = 1d;
        _previewGrip.DragStarted += (_, _) =>
        {
            if (_project is null) return;
            start = Mouse.GetPosition(viewport);
            original = PreviewDimensions();
            var transform = _sample.TransformToAncestor(viewport);
            scale = Math.Max(.001, (transform.Transform(new Point(1, 0)) - transform.Transform(new Point())).Length);
        };
        _previewGrip.DragDelta += (_, _) =>
        {
            if (_project is null) return;
            var delta = Mouse.GetPosition(viewport) - start;
            SetCustomPreview(ClampPreview(Math.Round(original.Width + delta.X / scale),
                Math.Round(original.Height + delta.Y / scale)));
        };
    }

    private void ConfigurePreviewSizeLabels()
    {
        var current = _previewSizeMode.SelectedValue ?? "opening";
        _previewSizeMode.ItemsSource = new[] { new Choice("opening", T("PreviewOpeningSize")),
            new Choice("minimum", T("PreviewMinimumSize")), new Choice("custom", T("PreviewCustomSize")) };
        _previewSizeMode.SelectedValue = current;
        SetCommandLabel(_applyPreviewSize, "PreviewApplySize");
        SetCommandLabel(_setPreviewSize, "PreviewSetSize");
        AutomationProperties.SetName(_previewSizeMode, T("PreviewSizeMode"));
        AutomationProperties.SetName(_previewGrip, T("PreviewResizeHint"));
        _previewGrip.ToolTip = T("PreviewResizeHint");
    }

    private void EditPreviewSize()
    {
        if (_project is null) return;
        var size = PreviewDimensions();
        var width = new TextBox { Text = size.Width.ToString(CultureInfo.CurrentCulture) };
        var height = new TextBox { Text = size.Height.ToString(CultureInfo.CurrentCulture) };
        var fields = new StackPanel();
        fields.Children.Add(DialogText("PreviewResizeHint"));
        DialogField(fields, width, "Width", "PreviewTestWidth");
        DialogField(fields, height, "Height", "PreviewTestHeight");
        var dialog = EditDialog(T("PreviewSetSize"), fields, out var buttons, 560, 340);
        var apply = EditDialogButtons(dialog, buttons, "PreviewResize", "PreviewTestApply", () => dialog.DialogResult = true);
        void Refresh() => apply.IsEnabled = TryReadDimension(width, out var w) && w >= _minWidthSlider.Value
            && TryReadDimension(height, out var h) && h >= _minHeightSlider.Value;
        width.TextChanged += (_, _) => Refresh();
        height.TextChanged += (_, _) => Refresh();
        Refresh();
        if (dialog.ShowDialog() == true && TryReadDimension(width, out var w) && TryReadDimension(height, out var h))
            SetCustomPreview(ClampPreview(w, h));
    }

    private void SetCustomPreview(Size size)
    {
        _customPreviewSize = size;
        _previewSizeMode.SelectedValue = "custom";
        UpdateCanvas();
    }
}
