using System.Windows;
using System.Windows.Input;
using AIHub.Services.OfficeEditor;
using Cursor = System.Windows.Input.Cursor;
using Cursors = System.Windows.Input.Cursors;
using Point = System.Windows.Point;

namespace AIHub.Controls.OfficeTextEditor;

internal sealed partial class OfficeSlideObjectOverlay
{
    [Flags]
    private enum Grip { None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8, Move = 16 }

    // Corners win when small objects have overlapping handle hit areas.
    private static readonly Grip[] Grips =
    [
        Grip.Left | Grip.Top, Grip.Right | Grip.Top, Grip.Right | Grip.Bottom, Grip.Left | Grip.Bottom,
        Grip.Top, Grip.Right, Grip.Bottom, Grip.Left
    ];

    private bool Ready => _slide is { Width: > 0, Height: > 0 } &&
        double.IsFinite(_slide.Width) && double.IsFinite(_slide.Height) && Valid(_viewport);

    private static bool Valid(Rect rect) => rect.Width > 0 && rect.Height > 0
        && double.IsFinite(rect.Left) && double.IsFinite(rect.Top) && double.IsFinite(rect.Right) && double.IsFinite(rect.Bottom);

    private static Point Center(Rect r) => new(r.X+r.Width/2, r.Y+r.Height/2);
    private static System.Windows.Media.RotateTransform Rotation(Rect r, OfficeSlideObject obj)
    {
        var p = Center(r);
        return new(obj.Angle?.Invoke() ?? 0, p.X, p.Y);
    }
    private static Point Unrotate(Point p, Rect r, OfficeSlideObject obj) => Rotation(r, obj).Inverse!.Transform(p);

    private Rect ToScreen(OfficeSlideBounds bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return Rect.Empty;
        return new Rect(_viewport.X + bounds.X * _viewport.Width / _slide!.Width,
            _viewport.Y + bounds.Y * _viewport.Height / _slide.Height,
            bounds.Width * _viewport.Width / _slide.Width, bounds.Height * _viewport.Height / _slide.Height);
    }

    private OfficeSlideObject? HitObject(Point point)
    {
        if (!Ready || !_viewport.Contains(point)) return null;
        var objects = _slide!.Objects;
        for (int i = objects.Count - 1; i >= 0; i--)
        {
            var box = ToScreen(objects[i].Bounds);
            if (Valid(box) && box.Contains(Unrotate(point, box, objects[i]))) return objects[i];
        }
        return null;
    }

    private static Point GripCenter(Grip grip, Rect rect) => new(
        grip.HasFlag(Grip.Left) ? rect.Left : grip.HasFlag(Grip.Right) ? rect.Right : rect.Left + rect.Width / 2,
        grip.HasFlag(Grip.Top) ? rect.Top : grip.HasFlag(Grip.Bottom) ? rect.Bottom : rect.Top + rect.Height / 2);

    private Grip HitGrip(Point point)
    {
        if (Selected == null || !Ready || !_viewport.Contains(point)) return Grip.None;
        var bounds = ToScreen(Selected.Bounds);
        if (!Valid(bounds)) return Grip.None;
        point = Unrotate(point, bounds, Selected);
        foreach (var grip in Grips)
        {
            var center = GripCenter(grip, bounds);
            if (Math.Abs(center.X - point.X) <= 5 && Math.Abs(center.Y - point.Y) <= 5) return grip;
        }
        return Grip.None;
    }

    private Rect Adjust(Rect rect, Grip grip, Vector delta)
    {
        if (grip == Grip.Move)
        {
            // Retain a small selectable area even when a user moves a box mostly off the slide.
            var visibleX = Math.Min(8, Math.Min(rect.Width, _viewport.Width) / 2);
            var visibleY = Math.Min(8, Math.Min(rect.Height, _viewport.Height) / 2);
            var x = Math.Clamp(rect.X + delta.X, _viewport.Left - rect.Width + visibleX, _viewport.Right - visibleX);
            var y = Math.Clamp(rect.Y + delta.Y, _viewport.Top - rect.Height + visibleY, _viewport.Bottom - visibleY);
            return new Rect(x, y, rect.Width, rect.Height);
        }
        var minWidth = Math.Min(5, rect.Width);
        var minHeight = Math.Min(5, rect.Height);
        var left = grip.HasFlag(Grip.Left) ? Math.Min(rect.Left + delta.X, rect.Right - minWidth) : rect.Left;
        var right = grip.HasFlag(Grip.Right) ? Math.Max(rect.Right + delta.X, rect.Left + minWidth) : rect.Right;
        var top = grip.HasFlag(Grip.Top) ? Math.Min(rect.Top + delta.Y, rect.Bottom - minHeight) : rect.Top;
        var bottom = grip.HasFlag(Grip.Bottom) ? Math.Max(rect.Bottom + delta.Y, rect.Top + minHeight) : rect.Bottom;
        bool corner = (grip & (Grip.Left | Grip.Right)) != 0 && (grip & (Grip.Top | Grip.Bottom)) != 0;
        if (corner && ((Selected?.IsPicture == true) != Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)))
        {
            // Project the pointer onto the original diagonal. The opposite corner stays fixed.
            var scale = ((right - left) * rect.Width + (bottom - top) * rect.Height) /
                (rect.Width * rect.Width + rect.Height * rect.Height);
            scale = Math.Max(scale, Math.Max(minWidth / rect.Width, minHeight / rect.Height));
            if (grip.HasFlag(Grip.Left)) left = right - rect.Width * scale;
            else right = left + rect.Width * scale;
            if (grip.HasFlag(Grip.Top)) top = bottom - rect.Height * scale;
            else bottom = top + rect.Height * scale;
        }
        return new Rect(new Point(left, top), new Point(right, bottom));
    }

    private static Cursor? CursorFor(Grip grip) => grip switch
    {
        Grip.Move => Cursors.SizeAll,
        Grip.Left or Grip.Right => Cursors.SizeWE,
        Grip.Top or Grip.Bottom => Cursors.SizeNS,
        (Grip.Left | Grip.Top) or (Grip.Right | Grip.Bottom) => Cursors.SizeNWSE,
        (Grip.Left | Grip.Bottom) or (Grip.Right | Grip.Top) => Cursors.SizeNESW,
        _ => null
    };
}
