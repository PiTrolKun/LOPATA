using System.Windows;
using System.Windows.Media;
using AIHub.Services;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Pen = System.Windows.Media.Pen;

namespace AIHub.Controls;

internal sealed class MusicViolinist : FrameworkElement
{
    internal MusicGenerationStage Stage { get; private set; }
    internal double Seconds { get; private set; }
    internal bool Animate { get; private set; }
    internal static Color StageColor(MusicGenerationStage stage) => stage switch
    {
        MusicGenerationStage.Loading => Color.FromRgb(115, 115, 115),
        MusicGenerationStage.Planning => Color.FromRgb(25, 52, 145),
        MusicGenerationStage.Sequence => Color.FromRgb(35, 147, 230),
        MusicGenerationStage.Sound or MusicGenerationStage.Encoding or MusicGenerationStage.Completed => Color.FromRgb(22, 145, 66),
        MusicGenerationStage.Error => Color.FromRgb(210, 40, 40),
        _ => Colors.Black
    };

    internal void Update(MusicGenerationSnapshot state, bool animate)
    { Stage = state.Stage; Seconds = state.Elapsed.TotalSeconds; Animate = animate && state.Active; InvalidateVisual(); }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        drawing.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(237, 240, 243)), null, new Rect(RenderSize), 8, 8);
        var scale = Math.Min(ActualWidth / 100, ActualHeight / 88);
        drawing.PushTransform(new TranslateTransform((ActualWidth - 100 * scale) / 2, (ActualHeight - 88 * scale) / 2));
        drawing.PushTransform(new ScaleTransform(scale, scale));
        var mirror = Animate && (int)(Seconds / 4) % 2 == 1;
        drawing.PushTransform(new ScaleTransform(mirror ? -1 : 1, 1, 50, 0));
        var blinking = Animate && Stage is MusicGenerationStage.Planning or MusicGenerationStage.Sequence or MusicGenerationStage.Sound;
        drawing.PushOpacity(blinking ? .3 + .7 * (1 + Math.Sin(Seconds * Math.PI * 2)) / 2 : 1);
        var pen = new Pen(new SolidColorBrush(StageColor(Stage)), 2.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        drawing.DrawEllipse(null, pen, new Point(40, 16), 8, 8);
        Line(40, 24, 40, 54); Line(40, 54, 29, 79); Line(40, 54, 49, 79);
        Line(40, 31, 51, 32); Line(51, 32, 69, 21);
        var stroke = Animate ? Math.Sin(Seconds * 5) * 7 : 0;
        Line(40, 32, 27, 42); Line(27, 42, 56 + stroke, 42 - stroke / 2);
        // Violin body, neck and strings; the bow moves across them.
        drawing.PushTransform(new RotateTransform(-29, 54, 31));
        drawing.DrawEllipse(null, pen, new Point(50, 31), 6, 4);
        drawing.DrawEllipse(null, pen, new Point(58, 31), 5, 3.5);
        Line(62, 31, 76, 31); Line(47, 31, 76, 31);
        drawing.Pop(); Line(46 + stroke, 20 - stroke / 2, 59 + stroke, 48 - stroke / 2);
        drawing.Pop(); drawing.Pop(); drawing.Pop(); drawing.Pop();
        void Line(double x1, double y1, double x2, double y2) => drawing.DrawLine(pen, new Point(x1, y1), new Point(x2, y2));
    }
}
