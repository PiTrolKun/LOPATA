using System.Windows;
using System.Windows.Controls;
using ComboBox = System.Windows.Controls.ComboBox;

namespace AIHub.Controls;

public sealed partial class SongClipControl
{
    private static readonly FrameSize[] OutputSizes = [new(640, 360), new(1280, 720), new(1920, 1080), new(720, 1280), new(720, 720)];
    private static readonly FrameSize[] MotionSizes = [new(512, 288), new(1024, 576), new(288, 512), new(576, 1024), new(512, 512)];
    private bool _syncingSizes;

    private readonly record struct FrameSize(int Width, int Height)
    {
        internal int Orientation => Width.CompareTo(Height);
        internal double Ratio => (double)Width / Height;
        public string Label => $"{Math.Min(Width, Height)}p";
        private double IconScale => 19d / Math.Max(1, Math.Max(Width, Height));
        public double IconWidth => Math.Max(0, Width) * IconScale;
        public double IconHeight => Math.Max(0, Height) * IconScale;
        public override string ToString() => $"{Width} × {Height}";
    }

    private void ConfigureSizePresentation()
    {
        var template = (DataTemplate)Resources["SongClipFrameSize"];
        foreach (var choice in new[] { _size, _motionSize, _geekSize, _infiniteSize })
        {
            choice.ItemTemplate = template;
            TextSearch.SetTextPath(choice, nameof(FrameSize.Label));
        }
    }

    private FrameSize OutputSize => _size.SelectedItem is FrameSize selected
        ? selected : new(_project.Settings.Width, _project.Settings.Height);

    private void ChangeOutputSize()
    {
        if (_loading || _busy || _syncingSizes) return;
        var motion = _motionSize.SelectedItem as FrameSize?;
        var geek = _geekSize.SelectedItem as FrameSize?;
        _syncingSizes = true;
        try
        {
            if (motion is FrameSize previousMotion) SelectGenerationSize(_motionSize, previousMotion);
            if (geek is FrameSize previousGeek) SelectGenerationSize(_geekSize, previousGeek);
        }
        finally { _syncingSizes = false; }
        Checkpoint();
    }

    private void SelectGenerationSize(ComboBox control, FrameSize previous, bool keepPending = false)
    {
        var output = OutputSize;
        var sameOrientation = MotionSizes.Where(size => size.Orientation == output.Orientation).ToArray();
        var exact = sameOrientation.Where(size => (long)size.Width * output.Height == (long)size.Height * output.Width).ToArray();
        if (exact.Length > 0) sameOrientation = exact;
        var closest = sameOrientation.OrderBy(size => Math.Abs(Math.Log(size.Ratio / output.Ratio)))
            .ThenBy(size => Math.Abs(size.Width * size.Height - (long)previous.Width * previous.Height)).First();
        var supported = previous.Width is >= 256 and <= 1536 && previous.Height is >= 256 and <= 1536
            && previous.Width % 32 == 0 && previous.Height % 32 == 0;
        var keep = supported && (keepPending || (long)previous.Width * output.Height == (long)previous.Height * output.Width);
        control.ItemsSource = keep && !sameOrientation.Contains(previous) ? [..sameOrientation, previous] : sameOrientation;
        control.SelectedItem = keep ? previous : closest;
        UpdateFrameSizeHint(control);
    }

    private void UpdateFrameSizeHint(ComboBox control)
    {
        var output = OutputSize;
        control.ToolTip = control.SelectedItem is FrameSize size && (long)size.Width * output.Height != (long)size.Height * output.Width
            ? _l("SongClip.AspectFitHint")
            : _l("SongClip.AspectMatchHint");
    }
}
