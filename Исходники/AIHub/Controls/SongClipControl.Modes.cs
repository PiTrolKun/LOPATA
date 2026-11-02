using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services.SongClip;
using ComboBox = System.Windows.Controls.ComboBox;
using Orientation = System.Windows.Controls.Orientation;

namespace AIHub.Controls;

public sealed partial class SongClipControl
{
    private readonly ComboBox _h3Mode = new(), _montageClipSeconds = new();
    private FrameworkElement? _montageDurationOption;
    private string? _loadedH3Mode;
    private bool _modeSettingsLoading;

    private FrameworkElement BuildModeChoice()
    {
        var option = ModeOption("ClipMode", _h3Mode);
        _h3Mode.Items.Add(new ComboBoxItem { Content = _l("SongClip.ConnectedMode"), Tag = "" });
        _h3Mode.Items.Add(new ComboBoxItem { Content = _l("SongClip.MontageMode"), Tag = "montage" });
        _h3Mode.ToolTip = _l("SongClip.ClipModeHint");
        return option;
    }

    private FrameworkElement BuildMontageDurationChoice()
    {
        _montageDurationOption = ModeOption("MontageClipSeconds", _montageClipSeconds);
        foreach (var seconds in new[] { 3, 5, 8, 10 }) _montageClipSeconds.Items.Add(seconds);
        _montageClipSeconds.ToolTip = _l("SongClip.MontageClipSecondsHint");
        return _montageDurationOption;
    }

    private FrameworkElement ModeOption(string key, ComboBox choice)
    {
        var option = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(4, 0, 12, 0) };
        var label = Label(key); label.VerticalAlignment = VerticalAlignment.Center;
        choice.MinWidth = 100; choice.Margin = new(6, 0, 0, 0);
        AutomationProperties.SetAutomationId(choice, "SongClip." + key);
        AutomationProperties.SetName(choice, _l("SongClip." + key));
        option.Children.Add(label); option.Children.Add(choice);
        return option;
    }

    private void LoadModeSettings()
    {
        _modeSettingsLoading = true;
        try
        {
            _loadedH3Mode = SongClipModes.Normalize(_project.Settings.Motion.Mode);
            _h3Mode.SelectedIndex = SongClipModes.IsMontage(_loadedH3Mode) ? 1 : 0;
            var montage = _project.Settings.Montage ?? new SongClipMontageSettings();
            var connected = _project.Settings.Motion;
            var (width, height, steps) = SongClipModes.IsMontage(_loadedH3Mode)
                ? (montage.Width, montage.Height, montage.Steps)
                : (connected.Width, connected.Height, connected.Steps);
            _motionSize.SelectedIndex = Array.FindIndex(MotionSizes, size => size.Width == width && size.Height == height);
            if (_motionSize.SelectedIndex < 0) _motionSize.SelectedIndex = 0;
            _motionSteps.SelectedItem = steps;
            _montageClipSeconds.SelectedItem = montage.ClipSeconds;
            RefreshModePresentation();
        }
        finally { _modeSettingsLoading = false; }
    }

    private void CaptureModeSettings()
    {
        if (SongClipModes.IsMontage(_loadedH3Mode))
        {
            var previous = _project.Settings.Montage;
            var settings = previous ?? new SongClipMontageSettings();
            var size = _motionSize.SelectedIndex >= 0 ? MotionSizes[_motionSize.SelectedIndex] : (settings.Width, settings.Height);
            var updated = settings with
            {
                Width = size.Item1, Height = size.Item2,
                Steps = _motionSteps.SelectedItem is int steps ? steps : settings.Steps,
                ClipSeconds = _montageClipSeconds.SelectedItem is int seconds ? seconds : settings.ClipSeconds
            };
            // An untouched fallback stays absent in historical documents and snapshots.
            if (previous is not null || updated != settings) _project.Settings.Montage = updated;
        }
        else
        {
            var settings = _project.Settings.Motion;
            var size = _motionSize.SelectedIndex >= 0 ? MotionSizes[_motionSize.SelectedIndex] : (settings.Width, settings.Height);
            // Replace the nested record so pending attempts keep their immutable inputs.
            _project.Settings.Motion = settings with
            {
                Width = size.Item1, Height = size.Item2,
                Steps = _motionSteps.SelectedItem is int steps ? steps : settings.Steps
            };
        }
    }

    private void ChangeClipMode()
    {
        if (_loading || _modeSettingsLoading || _busy) return;
        CaptureModeSettings();
        var mode = (_h3Mode.SelectedItem as ComboBoxItem)?.Tag as string;
        _project.Settings.Motion = _project.Settings.Motion with { Mode = SongClipModes.Normalize(mode) };
        LoadModeSettings(); RefreshEnginePresentation();
        Checkpoint(); RefreshActions();
    }

    private void RefreshModePresentation()
    {
        if (_montageDurationOption is not null)
            _montageDurationOption.Visibility = SongClipModes.IsMontage(_loadedH3Mode) ? Visibility.Visible : Visibility.Collapsed;
    }

    private string ResultLabel(SongClipResult result)
    {
        var engine = EngineName(result.Model);
        if (result.Model == "h3_music_context")
            engine += " · " + _l(SongClipModes.IsMontage(result.Mode) ? "SongClip.MontageMode" : "SongClip.ConnectedMode");
        return $"{engine} · {result.Seconds:F1} s · {result.CreatedUtc.ToLocalTime():g}";
    }
}
