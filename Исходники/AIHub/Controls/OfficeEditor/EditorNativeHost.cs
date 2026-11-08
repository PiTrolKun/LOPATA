using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;

namespace AIHub.Controls.OfficeEditor;

/// <summary>Real system-child surface; Windows/LibreOffice handle IME, caret, selection, and clipboard.</summary>
internal sealed class EditorNativeHost : HwndHost
{
    private nint _handle;
    internal nint HandleValue => _handle;
    internal nint DocumentHandle { get; set; }
    internal event Action? Resized;
    internal event Action? FocusRequested;
    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        _handle = CreateWindowEx(0, "STATIC", "LOPATA.Document", 0x50000000 | 0x02000000 | 0x04000000,
            0, 0, 100, 100, parent.Handle, 0, 0, 0);
        if (_handle == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return new(this, _handle);
    }
    protected override void DestroyWindowCore(HandleRef hwnd) { DestroyWindow(hwnd.Handle); _handle = 0; }
    protected override void OnWindowPositionChanged(Rect bounds) { base.OnWindowPositionChanged(bounds); Resized?.Invoke(); }
    protected override bool TabIntoCore(TraversalRequest request) { FocusRequested?.Invoke(); return true; }
    internal (int Width, int Height) PixelSize()
    {
        GetClientRect(_handle, out var rect); return (Math.Max(1, rect.Right), Math.Max(1, rect.Bottom));
    }
    [StructLayout(LayoutKind.Sequential)] private struct RectI { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(int ex, string cls, string title, int style, int x, int y, int w, int h, nint parent, nint menu, nint module, nint parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out RectI rect);
}
