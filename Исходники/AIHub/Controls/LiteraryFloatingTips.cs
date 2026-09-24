using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AIHub.Services;
using Size = System.Windows.Size;
using Panel = System.Windows.Controls.Panel;

namespace AIHub.Controls;

/// <summary>Bounded, staggered tip rotation; held balloons are skipped independently.</summary>
internal sealed class LiteraryFloatingTips : Panel
{
    private readonly IReadOnlyList<LiteraryTip> _tips;
    private readonly LiteraryTipDeck _deck;
    private readonly Func<bool> _isAnalysisActive;
    private readonly List<LiteraryTipBubble> _bubbles = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly Dictionary<LiteraryTipBubble, Rect> _places = [];
    private int _slot;
    private long _nextChange;
    private bool _running;

    public LiteraryFloatingTips(IReadOnlyList<LiteraryTip> tips, string holdHint, Func<bool> isAnalysisActive,
        string? historyPath = null)
    {
        _tips = tips; _isAnalysisActive = isAnalysisActive;
        _deck = new LiteraryTipDeck(tips.Select(t => (t.Id, t.Category)), historyPath);
        Margin = new Thickness(4, 12, 4, 8);
        for (var i = 0; i < Math.Min(3, tips.Count); i++)
        {
            var right = i % 2 != 0;
            var bubble = new LiteraryTipBubble(right, 3.8 + i * .8, holdHint);
            _bubbles.Add(bubble); Children.Add(bubble);
        }
        Loaded += (_, _) => RefreshActivity();
        IsVisibleChanged += (_, _) => RefreshActivity();
        Unloaded += (_, _) => Stop();
        _timer.Tick += (_, _) => Tick();
    }

    private bool CanRun() => IsLoaded && IsVisible && _isAnalysisActive();

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 480;
        for (var i = 0; i < _bubbles.Count; i++)
        {
            var bubble = _bubbles[i];
            bubble.Visibility = i < 2 || width >= 560 ? Visibility.Visible : Visibility.Collapsed;
            if (bubble.Visibility != Visibility.Visible) { bubble.SetActive(false); continue; }
            if (bubble.Tip is null && PickTip() is { } tip) bubble.SetTip(tip, false);
            bubble.Measure(new Size(Math.Max(40, Math.Min(440, width - 12)), double.PositiveInfinity));
        }
        return new Size(width, LayoutBubbles(width, false));
    }

    protected override Size ArrangeOverride(Size finalSize)
    { LayoutBubbles(finalSize.Width, true); return finalSize; }

    private double LayoutBubbles(double width, bool arrange)
    {
        var visible = _bubbles.Where(b => b.Visibility == Visibility.Visible).ToArray();
        var held = visible.FirstOrDefault(b => b.IsHeld && _places.ContainsKey(b));
        var pinned = held is null ? Rect.Empty : _places[held];
        double y = 6, bottom = 0;
        foreach (var bubble in visible)
        {
            var size = bubble.DesiredSize;
            var x = _bubbles.IndexOf(bubble) % 2 == 0 ? 6 : Math.Max(6, width - size.Width - 6);
            var top = y;
            if (bubble == held) { top = pinned.Y; x = pinned.X; }
            else if (held is not null && top < pinned.Bottom + 12 && top + size.Height + 12 > pinned.Y)
                top = pinned.Bottom + 12;
            var rect = new Rect(x, top, size.Width, size.Height);
            if (arrange) { bubble.Arrange(rect); _places[bubble] = rect; }
            y = Math.Max(y, rect.Bottom + 12); bottom = Math.Max(bottom, rect.Bottom);
        }
        return bottom + 6;
    }

    private LiteraryTip? PickTip()
    {
        var id = _deck.Next(_bubbles.Where(b => b.Tip is not null).Select(b => b.Tip!.Id).ToArray());
        return id is null ? null : _tips.First(t => t.Id == id);
    }

    private void RefreshActivity()
    {
        if (!CanRun()) { Stop(); return; }
        if (!_running)
        {
            _running = true;
            _nextChange = Environment.TickCount64 + 28000;
            _timer.Start();
        }
        foreach (var bubble in _bubbles) bubble.SetActive(bubble.Visibility == Visibility.Visible);
    }

    private void Tick()
    {
        if (!CanRun()) { Stop(); return; }
        // Also notice a system reduced-motion change while the screen remains open.
        foreach (var bubble in _bubbles) bubble.SetActive(bubble.Visibility == Visibility.Visible);
        Rotate(Environment.TickCount64);
    }

    private void Rotate(long now)
    {
        if (now < _nextChange) return;
        for (var offset = 0; offset < _bubbles.Count; offset++)
        {
            var index = (_slot + offset) % _bubbles.Count;
            var bubble = _bubbles[index];
            if (bubble.Visibility != Visibility.Visible || bubble.IsHeld || now < bubble.ReadyAfter) continue;
            bubble.ChangeTip(PickTip);
            _slot = (index + 1) % _bubbles.Count;
            _nextChange = now + 28000;
            return;
        }
    }

    private void Stop()
    {
        _timer.Stop(); _running = false;
        foreach (var bubble in _bubbles) bubble.SetActive(false);
    }
}
