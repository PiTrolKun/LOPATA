using System.Drawing;
using System.Runtime.InteropServices;

namespace AIHub.Services.MailHub;

internal static partial class MailHubActivity
{
    internal static async Task<bool> IsActiveAsync(CancellationToken token)
    {
        var before = ScreenFingerprint();
        var hadCursor = GetCursorPos(out var start);
        for (var i = 0; i < 4; i++)
        {
            await Task.Delay(500, token);
            if (hadCursor && GetCursorPos(out var position) && (start.X != position.X || start.Y != position.Y))
                return true;
            if (before is int previous && ScreenFingerprint() is int current && current != previous) return true;
        }
        return false;
    }

    private static int? ScreenFingerprint()
    {
        const int width = 96, height = 64;
        var rectangle = System.Windows.Forms.SystemInformation.VirtualScreen;
        if (rectangle.Width <= 0 || rectangle.Height <= 0) return null;
        using var thumbnail = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(thumbnail);
        var desktop = GetDC(IntPtr.Zero);
        if (desktop == IntPtr.Zero) return null;
        var target = graphics.GetHdc();
        bool captured;
        try
        {
            SetStretchBltMode(target, 4);
            captured = StretchBlt(target, 0, 0, width, height, desktop,
                rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height, 0x00CC0020);
        }
        finally { graphics.ReleaseHdc(target); ReleaseDC(IntPtr.Zero, desktop); }
        if (!captured) return null;
        var hash = new HashCode();
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            hash.Add(thumbnail.GetPixel(x, y));
        return hash.ToHashCode();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint { public int X; public int Y; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out CursorPoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StretchBlt(IntPtr target, int tx, int ty, int tw, int th,
        IntPtr source, int sx, int sy, int sw, int sh, int operation);
}
