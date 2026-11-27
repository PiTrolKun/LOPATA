using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls.VisualConstructor;

internal sealed partial class ConstructorEditor
{
    private const double MaximumDimension = 32767;
    private readonly Slider _widthSlider = CreateDimensionSlider();
    private readonly Slider _heightSlider = CreateDimensionSlider();
    private readonly Slider _minWidthSlider = CreateDimensionSlider();
    private readonly Slider _minHeightSlider = CreateDimensionSlider();
    private bool _syncingDimensions;

    private void AddDimensionFields()
    {
        var settings = new ResourceDictionary
        { Source = new Uri("/AIHub;component/Controls/SettingsResources.xaml", UriKind.Relative) };
        var style = (Style)settings[typeof(Slider)];
        Add("Width", _width, _widthSlider);
        Add("Height", _height, _heightSlider);
        Add("MinWidth", _minWidth, _minWidthSlider);
        Add("MinHeight", _minHeight, _minHeightSlider);

        void Add(string key, TextBox field, Slider slider)
        {
            var label = AddFieldLabel(key, field);
            slider.Style = style;
            slider.Margin = new Thickness(0, 0, 10, 0);
            AutomationProperties.SetAutomationId(slider, "VisualConstructor." + key + ".Slider");
            AutomationProperties.SetLabeledBy(slider, label);
            slider.ValueChanged += (_, _) =>
            {
                if (!_syncingDimensions && !_loading && _project is not null)
                    field.Text = slider.Value.ToString(CultureInfo.CurrentCulture);
            };

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
            Grid.SetColumn(field, 1);
            field.TextAlignment = TextAlignment.Right;
            row.Children.Add(slider);
            row.Children.Add(field);
            _properties.Children.Add(row);
        }
    }

    private static Slider CreateDimensionSlider() => new()
    {
        Minimum = 1, Maximum = 1, TickFrequency = 1, SmallChange = 1, LargeChange = 20,
        IsSnapToTickEnabled = true, IsMoveToPointEnabled = true, VerticalAlignment = VerticalAlignment.Center
    };

    private static bool TryReadDimension(TextBox field, out double value) =>
        double.TryParse(field.Text.Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value)
        && value is >= 1 and <= MaximumDimension;

    private void SyncDimensionSliders(bool resetRanges = false)
    {
        _syncingDimensions = true;
        try
        {
            SyncPair(_width, _minWidth, _widthSlider, _minWidthSlider, Math.Max(1920, SystemParameters.PrimaryScreenWidth));
            SyncPair(_height, _minHeight, _heightSlider, _minHeightSlider, Math.Max(1080, SystemParameters.PrimaryScreenHeight));
        }
        finally { _syncingDimensions = false; }

        void SyncPair(TextBox size, TextBox minimum, Slider sizeSlider, Slider minimumSlider, double standardMaximum)
        {
            var sizeValue = TryReadDimension(size, out var initialValue) ? initialValue : sizeSlider.Value;
            var minValue = TryReadDimension(minimum, out var lowerValue) ? lowerValue : minimumSlider.Value;
            var range = resetRanges ? standardMaximum : Math.Max(standardMaximum, sizeSlider.Maximum);
            sizeSlider.Minimum = minValue;
            sizeSlider.Maximum = Math.Min(MaximumDimension, Math.Ceiling(Math.Max(range, Math.Max(sizeValue, minValue))));
            sizeSlider.Value = sizeValue;
            minimumSlider.Maximum = sizeValue;
            minimumSlider.Value = minValue;
        }
    }
}
