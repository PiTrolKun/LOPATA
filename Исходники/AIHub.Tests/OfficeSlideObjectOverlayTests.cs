using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Grid = System.Windows.Controls.Grid;
using AIHub.Controls.OfficeTextEditor;
using AIHub.Services.OfficeEditor;
using Point = System.Windows.Point;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed partial class OfficeSlideObjectOverlayTests
{
    [TestMethod, TestCategory("Native")]
    public Task MouseManipulatesOnlyThePickedObjectInALetterboxedSlide()
    {
        if (Environment.GetEnvironmentVariable("LOPATA_SLIDE_OBJECT_GESTURES") != "1")
            Assert.Inconclusive("Set LOPATA_SLIDE_OBJECT_GESTURES=1 to run the desktop pointer test.");
        return OfficeProjectUiThread.RunAsync(Exercise);
    }

    private static async Task Exercise()
    {
        var box = new OfficeSlideBounds(100, 100, 400, 120);
        var photo = new OfficeSlideBounds(200, 110, 160, 100);
        var text = new OfficeSlideObject("b", "Title", false, () => box, changed => box = changed);
        var image = new OfficeSlideObject("p", "Photo", true, () => photo, changed => photo = changed, _ => { });
        var overlay = new OfficeSlideObjectOverlay(s => s);
        var viewport = new Rect(50, 45, 500, 250);
        overlay.SetSlide(new OfficeSlideCanvasModel(1000, 500, [text, image]));
        overlay.SetViewport(viewport);
        var commits = 0;
        overlay.Edited += () => commits++;
        var window = new Window { Width = 640, Height = 400, Content = overlay, WindowStyle = WindowStyle.None,
            ShowInTaskbar = false, Topmost = true, Left = 80, Top = 80, Title = "LOPATA slide object gesture test" };
        GetCursorPos(out var previous);
        try
        {
            window.Show(); window.Activate(); window.UpdateLayout();
            await Task.Delay(80);
            var handle = new WindowInteropHelper(window).Handle;
            Task Pointer(int x, int y, uint message, int flags = 0) => PointMouse(window, overlay, new Point(x,y), message, flags);
            const uint down = 0x0201, up = 0x0202, move = 0x0200;
            await Pointer(175, 125, down, 1); // The photo is in front of the text box.
            Assert.AreSame(image, overlay.Selected);
            await Pointer(210, 145, move, 1);
            Assert.AreEqual(0, commits);
            await Pointer(210, 145, up);
            Assert.AreEqual(1, commits);
            Near(270,photo.X); Near(150, photo.Y); Near(160,photo.Width);
            Assert.AreEqual(new OfficeSlideBounds(100,100,400,120),box);
            await Pointer(265,145,down,1); // Drag middle right handle of photo.
            await Pointer(290,145,move,1); await Pointer(290,145,up);
            Assert.AreEqual(2, commits);
            Near(210,photo.Width); Near(100,photo.Height); Near(270,photo.X);
            var savedPhoto = photo;
            await Pointer(225,140,down,1); await Pointer(230,167,move,1);
            SendMessage(handle,0x0100,0x1b,1); // Escape cancels the pending drag.
            await Pointer(230,167,up);
            Assert.AreEqual(2, commits); Assert.AreEqual(savedPhoto,photo);
            await Pointer(121,113,down,1); await Pointer(131,108,move,1); await Pointer(131,108,up);
            Assert.AreSame(text,overlay.Selected);
            Near(120,box.X); Near(90,box.Y); Assert.AreEqual(3,commits);
            await Pointer(310,150,down,1); // Bottom right of the selected text box.
            await Pointer(340,160,move,1); await Pointer(340,160,up);
            Near(460,box.Width); Near(140,box.Height); Assert.AreEqual(4,commits);
            overlay.SetViewport(Rect.Empty);
            Assert.AreSame(text,overlay.Selected);
            overlay.SetViewport(viewport);
            await Pointer(9,20,down,1); await Pointer(9,20,up);
            Assert.IsNull(overlay.Selected); Assert.AreEqual(4,commits);
        }
        finally { window.Close(); SetCursorPos(previous.X, previous.Y); }
    }

    private static async Task PointMouse(Window window, FrameworkElement element, Point point, uint message, int flags = 0)
    {
        var screen = element.PointToScreen(point);
        await PointMouseOnScreen(window, screen, message, flags);
    }

    private static async Task PointMouseOnScreen(Window window, Point screen, uint message, int flags = 0)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var pos = new PixelPoint { X = (int)Math.Round(screen.X), Y = (int)Math.Round(screen.Y) };
        Assert.IsTrue(SetCursorPos(pos.X, pos.Y));
        Assert.IsTrue(ScreenToClient(handle, ref pos));
        SendMessage(handle, message, flags, (pos.Y << 16) | (pos.X & 0xffff));
        await Task.Delay(25);
    }

    [TestMethod, TestCategory("Native")]
    public Task DragTracksPhysicalMouseDeltaWhenCanvasOriginMovesDuringCapture()
    {
        if (Environment.GetEnvironmentVariable("LOPATA_SLIDE_OBJECT_GESTURES") != "1")
            Assert.Inconclusive("Set LOPATA_SLIDE_OBJECT_GESTURES=1 to run the desktop pointer test.");
        return OfficeProjectUiThread.RunAsync(async () =>
        {
            var original = new OfficeSlideBounds(160,140,320,150);
            var current = original;
            var canvas = new OfficeSlideObjectOverlay(s => s);
            var frame = new Grid(); frame.Children.Add(canvas);
            var model = new OfficeSlideCanvasModel(1000,500,[new OfficeSlideObject("p","photo",true,() => current,v => current = v)]);
            var viewport = new Rect(10,15,500,250);
            canvas.SetSlide(model); canvas.SetViewport(viewport);
            var window = new Window { Left=80,Top=80,Width=640,Height=400,WindowStyle=WindowStyle.None,
                Content=frame,Topmost=true,ShowInTaskbar=false };
            GetCursorPos(out var previous);
            try
            {
                window.Show(); window.Activate(); window.UpdateLayout(); await Task.Delay(80);
                var dpi = VisualTreeHelper.GetDpi(canvas);
                var physical = canvas.PointToScreen(new Point(150,100));
                var start = new Point(Math.Round(physical.X),Math.Round(physical.Y));
                var delta = new Vector(40,-18);
                await PointMouseOnScreen(window,start,0x201,1);
                Assert.IsTrue(canvas.IsMouseCaptured);
                // A layout/focus change shifts the layer on screen; its model scale stays the same.
                frame.RenderTransform = new TranslateTransform(0,3 / dpi.DpiScaleY);
                window.UpdateLayout(); await Task.Delay(40);
                await PointMouseOnScreen(window,start + delta,0x200,1);
                await PointMouseOnScreen(window,start + delta,0x202);
                Assert.AreEqual(40d,(current.X-original.X) * viewport.Width / model.Width * dpi.DpiScaleX,.25);
                Assert.AreEqual(-18d,(current.Y-original.Y) * viewport.Height / model.Height * dpi.DpiScaleY,.25);
                Assert.AreEqual(original.Width,current.Width); Assert.AreEqual(original.Height,current.Height);
            }
            finally { window.Close(); SetCursorPos(previous.X,previous.Y); }
        });
    }
    private static void Near(double expected, double actual) => Assert.AreEqual(expected,actual,2.1);
    [StructLayout(LayoutKind.Sequential)] private struct PixelPoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out PixelPoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(nint window, ref PixelPoint point);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
}
