using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Brushes = System.Windows.Media.Brushes;

namespace AIHub.Controls.VoiceNotifications;

internal static class VoiceNotificationIcon
{
    internal static DrawingImage Create(bool enabled)
    {
        var drawing = new DrawingGroup();
        var speaker = new SolidColorBrush(enabled ? Color.FromRgb(42, 167, 255) : Color.FromRgb(145, 145, 145));
        var lips = new SolidColorBrush(enabled ? Color.FromRgb(247, 90, 119) : Color.FromRgb(190, 190, 190));
        drawing.Children.Add(new GeometryDrawing(speaker, new Pen(speaker, 1), Geometry.Parse("M1,10 L6,10 L12,5 L12,23 L6,18 L1,18 Z")));
        drawing.Children.Add(new GeometryDrawing(null, new Pen(speaker, 1.8), Geometry.Parse("M15,8 Q21,14 15,20 M18,4 Q28,14 18,24")));
        drawing.Children.Add(new GeometryDrawing(Brushes.Black, new Pen(lips, 2), Geometry.Parse("M18,21 Q22,14 26,19 Q30,15 35,21 Q27,32 18,21 Z")));
        drawing.Children.Add(new GeometryDrawing(Brushes.White, null, Geometry.Parse("M21,21 L32,21 Q27,25 21,21 Z")));
        drawing.Freeze(); var image = new DrawingImage(drawing); image.Freeze(); return image;
    }
}
