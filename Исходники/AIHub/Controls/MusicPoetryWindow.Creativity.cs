using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using AIHub.Services;
using Slider = System.Windows.Controls.Slider;
using TextBox = System.Windows.Controls.TextBox;
using TextBlock = System.Windows.Controls.TextBlock;

namespace AIHub.Controls;

public sealed partial class MusicPoetryWindow
{
    private double _creativityValue = .8;
    private bool _creativityFailed, _syncingCreativity;
    private Popup? _creativityPopup;
    private Slider? _creativitySlider;
    private TextBox? _creativityInput;
    private TextBlock? _creativityError;

    private void CloseCreativity() { if (_creativityPopup is not null) _creativityPopup.IsOpen = false; }

    private void LoadCreativity()
    {
        CloseCreativity(); _creativityFailed = false;
        if (MusicPoetryCreativity.ModelId(_model) is { } id)
        {
            try { _creativityValue = _preferences.Load().Creativity.GetValueOrDefault(id, MusicPoetryCreativity.Default(id)); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            { _creativityFailed = true; _status.Text = L("Selection.PreferencesError") + " " + ex.Message; }
        }
        CreativityCaption();
    }

    private void CreativityCaption()
    {
        _creativity.Content = MusicPoetryCreativity.ModelId(_model) is not null && !_creativityFailed
            ? $"{L("CreativityShort")}: {MusicPoetryCreativity.Format(_creativityValue, _interfaceLanguage())} ▾" : L("CreativityShort");
        _creativity.ToolTip = L("Creativity") + "\n" + L("CreativityHint");
        AutomationProperties.SetName(_creativity, L("Creativity"));
        AutomationProperties.SetHelpText(_creativity, _creativity.ToolTip.ToString());
    }

    private void OpenCreativity()
    {
        if (!_creativity.IsEnabled || _viewClosed || !_view.IsVisible || MusicPoetryCreativity.ModelId(_model) is not { } id) return;
        if (_creativityPopup?.IsOpen == true) { CloseCreativity(); return; }
        CloseThoughtScale();
        _reasoning.ContextMenu?.SetCurrentValue(System.Windows.Controls.ContextMenu.IsOpenProperty, false);
        var modelPath = _model!.Path;
        var popup = new Popup { PlacementTarget = _creativity, Placement = PlacementMode.Top,
            StaysOpen = false, AllowsTransparency = true };
        var content = new StackPanel(); content.Children.Add(Label(L("Creativity")));
        var slider = new Slider { Minimum = MusicPoetryCreativity.Minimum, Maximum = MusicPoetryCreativity.Maximum,
            Value = _creativityValue, TickFrequency = MusicPoetryCreativity.Step, IsSnapToTickEnabled = true,
            SmallChange = MusicPoetryCreativity.Step, LargeChange = .5, Margin = new(4, 8, 4, 8) };
        _creativitySlider = slider;
        AutomationProperties.SetAutomationId(slider, "Music.Poetry.Creativity.Slider");
        AutomationProperties.SetName(slider, L("Creativity"));
        content.Children.Add(slider);
        content.Children.Add(Label(L("CreativityInputHint")));
        var row = new DockPanel();
        var input = Area("Creativity.Value"); input.AcceptsReturn = input.AcceptsTab = false;
        input.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        input.Text = MusicPoetryCreativity.Format(_creativityValue, _interfaceLanguage());
        input.Margin = new(4); _creativityInput = input;
        AutomationProperties.SetName(input, L("Creativity"));
        var error = Label(""); error.Visibility = Visibility.Collapsed; _creativityError = error;
        var apply = Action(row, "Creativity.Apply", ApplyInput); DockPanel.SetDock(apply, Dock.Right);
        row.Children.Add(input); content.Children.Add(row);
        AutomationProperties.SetAutomationId(error, "Music.Poetry.Creativity.Error");
        content.Children.Add(error); content.Children.Add(Label(L("CreativityHint")));
        var border = new Border { Child = content, Padding = new(10), Width = 300,
            CornerRadius = new(8), BorderThickness = new(1), Resources = Resources };
        border.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        border.SetResourceReference(FontSizeProperty, "UiBodyFontSize");
        popup.Child = border;
        _creativityPopup = popup;
        slider.ValueChanged += (_, _) => {
            if (_syncingCreativity || !Current()) return;
            Save(Math.Round(slider.Value, 1, MidpointRounding.AwayFromZero));
        };
        input.PreviewKeyDown += (_, e) => {
            if (e.Key == Key.Enter) { e.Handled = true; ApplyInput(); }
        };
        content.PreviewKeyDown += (_, e) => {
            if (e.Key == Key.Escape) { e.Handled = true; CloseCreativity(); _creativity.Focus(); }
        };
        popup.IsOpen = true; slider.Focus();

        bool Current() => popup.IsOpen && _creativity.IsEnabled && _model?.Path == modelPath
            && !IsWorking && _background?.IsRunning != true && _background?.BlocksNewWork != true;
        void ApplyInput()
        {
            if (!Current()) return;
            if (!MusicPoetryCreativity.TryParse(input.Text, out var value))
            { error.Text = L("CreativityInvalid"); error.Visibility = Visibility.Visible; return; }
            Save(value);
        }
        void Save(double value)
        {
            try
            {
                _preferences.SetCreativity(id, value);
                _creativityValue = value; _creativityFailed = false; CreativityCaption(); Availability();
                error.Text = ""; error.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                error.Text = _status.Text = L("Selection.PreferencesError") + " " + ex.Message;
                error.Visibility = Visibility.Visible;
            }
            _syncingCreativity = true;
            try { slider.Value = _creativityValue; input.Text = MusicPoetryCreativity.Format(_creativityValue, _interfaceLanguage()); }
            finally { _syncingCreativity = false; }
        }
    }
}
