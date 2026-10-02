using System.Runtime.InteropServices;
using System.Windows;

namespace AIHub.Services;

public sealed record CaptureDisplay(nint Handle, Int32Rect Bounds);

public static class CaptureDisplayGeometry
{
    public static IReadOnlyList<CaptureDisplay> Displays()
    {
        var result = new List<CaptureDisplay>();
        var previous = SetThreadDpiAwarenessContext(new(-4));
        try
        {
            MonitorCallback callback = (nint monitor, nint dc, ref NativeRect bounds, nint data) =>
            { result.Add(new(monitor, new(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top))); return true; };
            if (!EnumDisplayMonitors(0, 0, callback, 0)) throw new System.ComponentModel.Win32Exception();
            if (result.Count == 0) throw new InvalidOperationException("No display available.");
            return result;
        }
        finally { if (previous != 0) SetThreadDpiAwarenessContext(previous); }
    }
    public static CaptureDisplay AtCursor()
    {
        if (!GetPhysicalCursorPos(out var point)) throw new System.ComponentModel.Win32Exception();
        var handle = MonitorFromPoint(point, 2);
        return Displays().First(d => d.Handle == handle);
    }
    public static Int32Rect Union(IEnumerable<CaptureDisplay> displays)
    {
        var items = displays.ToArray();
        var left = items.Min(d => d.Bounds.X); var top = items.Min(d => d.Bounds.Y);
        return new(left, top, items.Max(d => d.Bounds.X + d.Bounds.Width) - left,
            items.Max(d => d.Bounds.Y + d.Bounds.Height) - top);
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    private delegate bool MonitorCallback(nint monitor, nint dc, ref NativeRect rect, nint data);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetPhysicalCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
}
