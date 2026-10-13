using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using AIHub.Services;
using Slider = System.Windows.Controls.Slider;
using TextBlock = System.Windows.Controls.TextBlock;
using ContextMenu = System.Windows.Controls.ContextMenu;

namespace AIHub.Controls;

public sealed partial class MusicPoetryWindow
{
    private int _thoughtFontIncrease = MusicPoetryThoughtAppearance.DefaultIncrease;
    private Popup? _thoughtScalePopup;
    private Slider? _thoughtScaleSlider;
    private bool _syncingThoughtScale;

    private void InitializeThoughtAppearance()
    {
        try { _thoughtFontIncrease = _preferences.Load().ThoughtFontIncreasePercent; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        { _status.Text = L("Selection.PreferencesError") + " " + ex.Message; }
        _reasoning.PreviewMouseRightButtonUp += (_, e) => { e.Handled = true; OpenThoughtScale(); };
        _reasoning.ContextMenuOpening += (_, e) => {
            e.Handled = true; if (_thoughtScalePopup?.IsOpen != true) OpenThoughtScale();
        };
        _reasoning.PreviewKeyDown += (_, e) => {
            if (e.Key == Key.Apps || e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift)
            { e.Handled = true; OpenThoughtScale(); }
        };
    }

    private void CloseThoughtScale() { if (_thoughtScalePopup is not null) _thoughtScalePopup.IsOpen = false; }

    private void OpenThoughtScale()
    {
        if (!_reasoning.IsEnabled || _viewClosed || !_view.IsVisible) return;
        if (_thoughtScalePopup?.IsOpen == true) { CloseThoughtScale(); return; }
        _reasoning.ContextMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
        var content = new StackPanel(); content.Children.Add(new TextBlock { Text = L("ThoughtFontTitle"), TextWrapping = TextWrapping.Wrap });
        var value = new TextBlock { Margin = new(0, 6, 0, 4), Text = string.Format(L("ThoughtFontValue"), _thoughtFontIncrease) };
        content.Children.Add(value);
        var slider = new Slider { Minimum = 0, Maximum = MusicPoetryThoughtAppearance.MaximumIncrease,
            Value = _thoughtFontIncrease, TickFrequency = 10, IsSnapToTickEnabled = true,
            SmallChange = 10, LargeChange = 100, Margin = new(0, 4, 0, 4) };
        _thoughtScaleSlider = slider;
        AutomationProperties.SetAutomationId(slider, "Music.Poetry.ThoughtFontScale");
        AutomationProperties.SetName(slider, L("ThoughtFontTitle"));
        content.Children.Add(slider);
        content.Children.Add(new TextBlock { Text = L("ThoughtFontHint"), TextWrapping = TextWrapping.Wrap });
        var border = new Border { Child = content, Padding = new(12), Width = 280,
            CornerRadius = new(8), BorderThickness = new(1), Resources = Resources };
        border.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        content.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        content.SetResourceReference(TextBlock.FontSizeProperty, "UiBodyFontSize");
        var popup = new Popup { PlacementTarget = _reasoning, Placement = PlacementMode.Top,
            StaysOpen = false, AllowsTransparency = true, Child = border };
        _thoughtScalePopup = popup;
        slider.ValueChanged += (_, _) => {
            if (_syncingThoughtScale) return;
            var percent = (int)Math.Round(slider.Value);
            if (percent == _thoughtFontIncrease) return;
            try
            {
                _preferences.SetThoughtFontIncrease(percent);
                _thoughtFontIncrease = percent; ResizeThoughts();
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                _syncingThoughtScale = true;
                try { slider.Value = _thoughtFontIncrease; } finally { _syncingThoughtScale = false; }
                _status.Text = L("Selection.PreferencesError") + " " + ex.Message;
            }
            value.Text = string.Format(L("ThoughtFontValue"), _thoughtFontIncrease);
        };
        slider.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { popup.IsOpen = false; e.Handled = true; } };
        popup.IsOpen = true; slider.Focus();
    }
}
