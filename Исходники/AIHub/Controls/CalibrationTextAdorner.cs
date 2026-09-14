using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using AIHub.Services;
using TextBox = System.Windows.Controls.TextBox;
using Point = System.Windows.Point;
using Brush = System.Windows.Media.Brush;

namespace AIHub.Controls;

// Repaint the text view's actual shaped glyphs through a range clip. The TextBox,
// spelling decorations, text, selection and undo stack remain entirely unchanged.
public sealed class CalibrationTextAdorner(TextBox input) : Adorner(input)
{
    public IReadOnlyList<CalibrationSpan> Spans { get; set; } = [];
    protected override void OnRender(DrawingContext context)
    {
        if(Spans.Count==0)return;
        var clip=new GeometryGroup { FillRule=FillRule.Nonzero };
        foreach(var span in Spans)
            for(var i=span.Start;i<span.Start+span.Length && i<input.Text.Length;i++)
            {
                if(char.IsLowSurrogate(input.Text[i])||input.Text[i] is '\r' or '\n')continue;
                var a=input.GetRectFromCharacterIndex(i,false); var b=input.GetRectFromCharacterIndex(i,true);
                if(a.IsEmpty||b.IsEmpty||a.Y!=b.Y)continue;
                var width=Math.Abs(b.X-a.X);if(width<=0)continue;
                clip.Children.Add(new RectangleGeometry(new Rect(Math.Min(a.X,b.X),a.Y,width,a.Height)));
            }
        if(clip.Children.Count==0)return;
        var foreground=input.Foreground as SolidColorBrush;
        var lightText=foreground is not null && foreground.Color.R+foreground.Color.G+foreground.Color.B>450;
        Brush red=new SolidColorBrush(lightText?System.Windows.Media.Color.FromRgb(255,112,112):System.Windows.Media.Color.FromRgb(180,0,0));
        context.PushClip(new RectangleGeometry(new Rect(input.RenderSize)));
        context.PushClip(clip);
        void Draw(Drawing? drawing)
        {
            if(drawing is GlyphRunDrawing glyph){context.DrawGlyphRun(red,glyph.GlyphRun);return;}
            if(drawing is not DrawingGroup group)return;
            context.PushTransform(group.Transform??Transform.Identity);
            if(group.ClipGeometry is not null)context.PushClip(group.ClipGeometry);
            foreach(var child in group.Children)Draw(child);
            if(group.ClipGeometry is not null)context.Pop();context.Pop();
        }
        void Visit(Visual visual)
        {
            var transform=visual.TransformToAncestor(input);
            var origin=transform.Transform(new Point());var x=transform.Transform(new Point(1,0));var y=transform.Transform(new Point(0,1));
            context.PushTransform(new MatrixTransform(x.X-origin.X,x.Y-origin.Y,y.X-origin.X,y.Y-origin.Y,origin.X,origin.Y));
            Draw(VisualTreeHelper.GetDrawing(visual));context.Pop();
            for(var i=0;i<VisualTreeHelper.GetChildrenCount(visual);i++)if(VisualTreeHelper.GetChild(visual,i) is Visual child)Visit(child);
        }
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(input);i++)if(VisualTreeHelper.GetChild(input,i) is Visual visual)Visit(visual);
        context.Pop();context.Pop();
    }
}
