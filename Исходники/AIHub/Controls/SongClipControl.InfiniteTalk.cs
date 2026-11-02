using System.Windows;
using System.Windows.Controls;
using AIHub.Services.SongClip;
using ComboBox = System.Windows.Controls.ComboBox;

namespace AIHub.Controls;

public sealed partial class SongClipControl
{
    private readonly WrapPanel _infiniteOptions = new();
    private readonly ComboBox _infiniteSize = new(), _infiniteSteps = new(), _infiniteAudioCfg = new(), _infiniteVramReserve = new();
    private static readonly (int Width, int Height)[] InfiniteSizes = [(448,448),(512,512),(640,384),(384,640),(832,480),(480,832)];
    private bool IsInfiniteTalk => _project.Model == InfiniteTalkRunner.ModelId;

    private FrameworkElement BuildInfiniteTalkOptions()
    {
        var title = Label("InfiniteParameters"); title.FontWeight = FontWeights.SemiBold;
        title.ToolTip = _l("SongClip.InfiniteHint"); _infiniteOptions.Children.Add(title);
        foreach (var (key, choice) in new[] { ("InfiniteSize",_infiniteSize), ("InfiniteSteps",_infiniteSteps),
            ("InfiniteAudioCfg",_infiniteAudioCfg), ("InfiniteVramReserve",_infiniteVramReserve) }) {
            _infiniteOptions.Children.Add(ModeOption(key,choice));
            choice.SelectionChanged += (_,_) => { if (!_loading && !_busy) Checkpoint(); };
        }
        foreach (var (width,height) in InfiniteSizes) _infiniteSize.Items.Add($"{width} × {height}");
        foreach (var steps in new[] {20,30,40}) _infiniteSteps.Items.Add(steps);
        foreach (var value in new[] {2d,3d,4d,5d}) _infiniteAudioCfg.Items.Add(value);
        foreach (var value in new[] {1d,2d,3d,4d}) _infiniteVramReserve.Items.Add(value);
        return _infiniteOptions;
    }

    private void LoadInfiniteTalkSettings()
    {
        var settings = _project.Settings.InfiniteTalk ?? new();
        _infiniteSize.SelectedIndex = Math.Max(0,Array.FindIndex(InfiniteSizes,s => s == (settings.Width,settings.Height)));
        _infiniteSteps.SelectedItem = settings.Steps; _infiniteAudioCfg.SelectedItem = settings.AudioCfg;
        _infiniteVramReserve.SelectedItem = settings.VramReserveGb;
    }

    private void CaptureInfiniteTalkSettings()
    {
        if (!IsInfiniteTalk) return;
        var settings = _project.Settings.InfiniteTalk ?? new();
        var size = _infiniteSize.SelectedIndex >= 0 ? InfiniteSizes[_infiniteSize.SelectedIndex] : (settings.Width,settings.Height);
        _project.Settings.InfiniteTalk = settings with { Width = size.Item1, Height = size.Item2,
            Steps = _infiniteSteps.SelectedItem is int steps ? steps : settings.Steps,
            AudioCfg = _infiniteAudioCfg.SelectedItem is double guidance ? guidance : settings.AudioCfg,
            VramReserveGb = _infiniteVramReserve.SelectedItem is double reserve ? reserve : settings.VramReserveGb };
    }
}
