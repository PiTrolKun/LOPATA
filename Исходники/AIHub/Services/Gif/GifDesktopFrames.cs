using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;

namespace AIHub.Services;

/// <summary>CPU-oriented GDI collection for a monitor or a physical desktop rectangle.
/// It needs no D3D device owned by LOPATA; Windows composition may still use GPU.</summary>
public static class GifDesktopFrames
{
    public static GifPixels Grab(Int32Rect bounds, int percent, bool cursor)
    {
        var width = Math.Max(1, (int)Math.Round(bounds.Width * percent / 100d));
        var height = Math.Max(1, (int)Math.Round(bounds.Height * percent / 100d));
        if ((long)width * height > 64_000_000) throw new InvalidOperationException("Capture exceeds the 64 megapixel limit.");
        var previousDpi = SetThreadDpiAwarenessContext(new(-4));
        nint screen = 0, dc = 0, bitmap = 0, old = 0;
        try
        {
            screen = GetDC(0); dc = CreateCompatibleDC(screen);
            var info = new BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, Bits = 32 };
            bitmap = CreateDIBSection(screen, ref info, 0, out var bits, 0, 0);
            if (screen == 0 || dc == 0 || bitmap == 0) throw new Win32Exception();
            old = SelectObject(dc, bitmap); SetStretchBltMode(dc, 4);
            if (!StretchBlt(dc, 0, 0, width, height, screen, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x40CC0020)) throw new Win32Exception();
            if (cursor)
            {
                var data = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
                if (GetCursorInfo(ref data) && (data.Flags & 1) != 0 && GetIconInfo(data.Cursor, out var icon))
                {
                    try
                    {
                        var sx = width / (double)bounds.Width; var sy = height / (double)bounds.Height;
                        DrawIconEx(dc, (int)Math.Round((data.X - icon.HotX - bounds.X) * sx),
                            (int)Math.Round((data.Y - icon.HotY - bounds.Y) * sy), data.Cursor,
                            Math.Max(1, (int)Math.Round(GetSystemMetrics(13) * sx)), Math.Max(1, (int)Math.Round(GetSystemMetrics(14) * sy)), 0, 0, 3);
                    }
                    finally { if (icon.Mask != 0) DeleteObject(icon.Mask); if (icon.Color != 0) DeleteObject(icon.Color); }
                }
            }
            var bytes = new byte[checked(width * height * 4)]; Marshal.Copy(bits, bytes, 0, bytes.Length);
            for (var i = 3; i < bytes.Length; i += 4) bytes[i] = 255;
            return new(width, height, bytes);
        }
        finally
        {
            if (old != 0) SelectObject(dc, old); if (bitmap != 0) DeleteObject(bitmap);
            if (dc != 0) DeleteDC(dc); if (screen != 0) ReleaseDC(0, screen);
            if (previousDpi != 0) SetThreadDpiAwarenessContext(previousDpi);
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public uint Size; public int Width, Height; public ushort Planes, Bits; public uint Compression, ImageSize; public int Xppm, Yppm; public uint Used, Important; }
    [StructLayout(LayoutKind.Sequential)] private struct CursorInfo { public int Size; public uint Flags; public nint Cursor; public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct IconInfo { public int IsIcon; public uint HotX, HotY; public nint Mask, Color; }
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(nint dc, int mode);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool StretchBlt(nint dc, int x, int y, int w, int h, nint from, int sx, int sy, int sw, int sh, uint operation);
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(nint icon, out IconInfo info);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(nint dc, int x, int y, nint icon, int w, int h, uint step, nint brush, uint flags);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
}
