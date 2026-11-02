using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services.SongClip;
using ComboBox = System.Windows.Controls.ComboBox;

namespace AIHub.Controls;

public sealed partial class SongClipControl
{
    private readonly WrapPanel _geekOptions = new();
    private readonly ComboBox _geekSize = new(), _geekSteps = new(), _geekCardSteps = new(), _geekSeconds = new(), _geekStrength = new();
    private bool IsGeek => _project.Model == GeekatplayRunner.ModelId;
    private bool HasSceneReferences => IsMotion || IsGeek || IsInfiniteTalk;
    private FrameworkElement BuildGeekOptions()
    {
        var title = Label("GeekParameters"); title.FontWeight = FontWeights.SemiBold;
        title.ToolTip = _l("SongClip.GeekHint"); _geekOptions.Children.Add(title);
        foreach (var (key, choice) in new[] { ("GeekSize",_geekSize), ("GeekSteps",_geekSteps), ("GeekCardSteps",_geekCardSteps),
            ("GeekSeconds",_geekSeconds), ("GeekStrength",_geekStrength) }) {
            _geekOptions.Children.Add(ModeOption(key,choice));
            choice.SelectionChanged += (_,_) => { if (!_loading && !_busy) Checkpoint(); };
        }
        foreach (var (width,height) in MotionSizes) _geekSize.Items.Add($"{width} × {height}");
        foreach (var steps in new[] {12,16,20,24,30,40}) _geekSteps.Items.Add(steps);
        foreach (var steps in new[] {8,16,20,30,40}) _geekCardSteps.Items.Add(steps);
        foreach (var seconds in new[] {5,6,8,10}) _geekSeconds.Items.Add(seconds);
        foreach (var value in new[] {.35,.55,.75,1.0}) _geekStrength.Items.Add(value);
        return _geekOptions;
    }
    private void LoadGeekSettings()
    {
        var settings = _project.Settings.Geek ?? new();
        _geekSize.SelectedIndex = Math.Max(0,Array.FindIndex(MotionSizes,s => s == (settings.Width,settings.Height)));
        _geekSteps.SelectedItem = settings.Steps; _geekCardSteps.SelectedItem = settings.CardSteps;
        _geekSeconds.SelectedItem = settings.ClipSeconds; _geekStrength.SelectedItem = settings.ReferenceStrength;
    }
    private void CaptureGeekSettings()
    {
        if (!IsGeek) return;
        var settings = _project.Settings.Geek ?? new();
        var size = _geekSize.SelectedIndex >= 0 ? MotionSizes[_geekSize.SelectedIndex] : (settings.Width,settings.Height);
        _project.Settings.Geek = settings with { Width = size.Item1, Height = size.Item2,
            Steps = _geekSteps.SelectedItem is int steps ? steps : settings.Steps,
            CardSteps = _geekCardSteps.SelectedItem is int cards ? cards : settings.CardSteps,
            ClipSeconds = _geekSeconds.SelectedItem is int seconds ? seconds : settings.ClipSeconds,
            ReferenceStrength = _geekStrength.SelectedItem is double strength ? strength : settings.ReferenceStrength };
    }
}
