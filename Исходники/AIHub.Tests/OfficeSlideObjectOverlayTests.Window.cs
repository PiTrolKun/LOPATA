using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Controls.OfficeReader;
using AIHub.Controls.OfficeTextEditor;
using AIHub.Services;
using AIHub.Services.OfficeEditor;
using Image = System.Windows.Controls.Image;
using Point = System.Windows.Point;

namespace AIHub.Tests;

public sealed partial class OfficeSlideObjectOverlayTests
{
    [TestMethod, TestCategory("Native"), DataRow(".pptx"), DataRow(".odp")]
    public Task RealDeckObjectsCanBeMovedResizedAndReplacedThroughTheEditor(string extension)
    {
        if (Environment.GetEnvironmentVariable("LOPATA_SLIDE_OBJECT_WINDOW") != "1")
            Assert.Inconclusive("Set LOPATA_SLIDE_OBJECT_WINDOW=1 to drive the presentation window and file dialog.");
        return OfficeProjectUiThread.RunAsync(() => ExerciseWindow(extension));
    }

    private static async Task ExerciseWindow(string extension)
    {
        var source = Path.Combine(AppDataPaths.ProjectRoot!, "Тесты", "OfficeReader_20261107", "user-suite",
            "AI_Office_TestSuite_RU_EN", "04_presentations", "RU", "complex_8_slides" + extension);
        var sourceBytes = await File.ReadAllBytesAsync(source);
        var root = Environment.GetEnvironmentVariable("LOPATA_SLIDE_OBJECT_EVIDENCE");
        var folder = string.IsNullOrWhiteSpace(root) ? Directory.CreateTempSubdirectory("lopata-slide-ui-").FullName :
            Directory.CreateDirectory(Path.Combine(root, extension[1..] + "-" + Guid.NewGuid().ToString("N"))).FullName;
        var copy = Path.Combine(folder, Path.GetFileName(source));
        var replacement = Path.Combine(folder, "new-picture.png");
        var shot = Path.Combine(folder, "reopened-slide.png");
        var report = Path.Combine(folder, "window.json");
        await File.WriteAllBytesAsync(copy, sourceBytes);
        WriteMarkerImage(replacement);
        var slide = OfficeNativeDocument.Open(sourceBytes, extension).SlideCanvas(3)!;
        var picture = slide.Objects.Single(s => s.Name == "Picture 4");
        var title = slide.Objects.Single(s => s.Name == "TextBox 2");
        var loc = new LocalizationService(); loc.Load("ru");
        var group = new ReaderWindowGroup(null, loc.T, false);
        var oldGate = ComponentLicenseGate.ConfirmAsync;
        ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
        GetPhysicalCursorPos(out var previous);
        ReaderWindow? window = null;
        try
        {
            window = group.OpenEditor(copy);
            var (canvas, image) = await SelectSlideFour(window);
            var initialImage = (BitmapSource)image.Source!;
            var area = SlideArea(canvas, initialImage);
            Point ScreenPoint(OfficeSlideBounds b, double fx, double fy) => new(
                area.X + area.Width * (b.X + fx * b.Width) / slide.Width,
                area.Y + area.Height * (b.Y + fy * b.Height) / slide.Height);
            var firstPoint = ScreenPoint(picture.Bounds, .48, .45);
            var hit = window.InputHitTest(canvas.TranslatePoint(firstPoint, window));
            var received = new List<string>();
            window.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler((_,e) =>
                received.Add(e.GetPosition(canvas) + " source=" + e.OriginalSource?.GetType().Name)),true);
            async Task Drag(Point point, Vector by)
            {
                await AimPointer(window, canvas, point);
                MouseButton(2);
                try
                {
                    await Task.Delay(65);
                    await AimPointer(window, canvas, point + by);
                }
                finally { MouseButton(4); }
                await Task.Delay(120);
            }
            await Drag(firstPoint, new Vector(12,-8));
            Assert.AreEqual(picture.Id, canvas.Selected?.Id, $"Pointer {firstPoint} on {area}. Layer {canvas.RenderSize}, " +
                $"hit {hit?.GetType().Name}, directly over {Mouse.DirectlyOver?.GetType().Name}, received {string.Join(", ", received)}.");
            await Drag(ScreenPoint(canvas.Selected!.Bounds, 1, .5), new Vector(-17,0));
            var editedPicture = canvas.Selected.Bounds;
            Assert.IsGreaterThan(picture.Bounds.X, editedPicture.X);
            Assert.IsLessThan(picture.Bounds.Width, editedPicture.Width);
            var picker = OfficeSheetWindowTests.CommonDialog(window, replacement);
            var button = Desc<Button>(window,"Office.Native.ReplacePicture");
            Assert.IsNotNull(button); Assert.IsTrue(button.IsEnabled);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await picker;
            bool PictureRepainted() => image.Source is BitmapSource bitmap && !ReferenceEquals(initialImage,bitmap) &&
                HasMarkerAt(bitmap, (editedPicture.X + editedPicture.Width * .49) / slide.Width,
                    (editedPicture.Y + editedPicture.Height * .45) / slide.Height);
            await Wait(PictureRepainted, "replacement rendered in the editor");
            if (!string.IsNullOrWhiteSpace(root))
            {
                var view = (FrameworkElement)window.Content;
                view.UpdateLayout();
                var frame = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth),(int)Math.Ceiling(view.ActualHeight),96,96,PixelFormats.Pbgra32);
                frame.Render(view);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(frame));
                using var stream = File.Create(Path.Combine(folder,"selected-picture-window.png")); encoder.Save(stream);
            }
            await Drag(ScreenPoint(title.Bounds, .46, .5), new Vector(8,10));
            Assert.AreEqual(title.Id,canvas.Selected?.Id);
            await Drag(ScreenPoint(canvas.Selected!.Bounds, 0, .5),new Vector(12,0));
            var editedTitle = canvas.Selected.Bounds;
            Assert.IsGreaterThan(title.Bounds.Y,editedTitle.Y);
            Assert.IsLessThan(title.Bounds.Width,editedTitle.Width);
            Desc<Button>(window,"Office.Editor.Save")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => File.Exists(copy + ".bak") &&
                Desc<TextBox>(window,"Office.Native.Block.0")?.IsEnabled == true, "native deck save finished");
            Assert.IsTrue(await window.CloseEditorAsync());
            window = group.OpenEditor(copy);
            var (_, reopenedImage) = await SelectSlideFour(window);
            var reopened = (BitmapSource)reopenedImage.Source!;
            Assert.IsTrue(HasMarkerAt(reopened, (editedPicture.X + editedPicture.Width * .49) / slide.Width,
                (editedPicture.Y + editedPicture.Height * .45) / slide.Height), "new picture after reopening");
            var saved = OfficeNativeDocument.Open(await File.ReadAllBytesAsync(copy),extension).SlideCanvas(3)!;
            Assert.AreEqual(editedPicture, saved.Objects.Single(s => s.Id == picture.Id).Bounds);
            Assert.AreEqual(editedTitle, saved.Objects.Single(s => s.Id == title.Id).Bounds);
            CollectionAssert.AreEqual(sourceBytes, await File.ReadAllBytesAsync(copy + ".bak"));
            CollectionAssert.AreEqual(sourceBytes, await File.ReadAllBytesAsync(source));
            if (!string.IsNullOrWhiteSpace(root))
            {
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(reopened));
                using (var stream = File.Create(shot)) encoder.Save(stream);
                await File.WriteAllTextAsync(report,JsonSerializer.Serialize(new { format = extension, slide = 4,
                    sourceSha256 = Convert.ToHexString(SHA256.HashData(sourceBytes)), originalPicture = picture.Bounds,
                    editedPicture, originalTitle = title.Bounds, editedTitle, replacementFileDialog = true,
                    replacementPixelsAfterReopening = true, backupAndOriginalIntact = true },
                    new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        finally
        {
            if (window is { IsVisible: true })
            {
                var discard = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                discard.Tick += (_, _) => { foreach (Window modal in window.OwnedWindows)
                    Desc<Button>(modal,"Office.Editor.Close.Discard")?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };
                discard.Start();
                try { await window.CloseEditorAsync(); } finally { discard.Stop(); }
            }
            SetPhysicalCursorPos(previous.X,previous.Y);
            ComponentLicenseGate.ConfirmAsync = oldGate;
            if (string.IsNullOrWhiteSpace(root))
            {
                foreach (var path in new[] { copy, copy + ".bak", replacement }) File.Delete(path);
                if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
            }
        }
    }

    private static async Task<(OfficeSlideObjectOverlay, Image)> SelectSlideFour(ReaderWindow window)
    {
        window.Topmost = true;
        window.Activate();
        await Wait(() => Desc<ListBox>(window,"Office.Native.Pages")?.Items.Count == 8, "slide list");
        Desc<ListBox>(window,"Office.Native.Pages")!.SelectedIndex = 3;
        await Wait(() => Desc<Image>(window,"Office.Editor.Native.Preview.Image") is { Source: BitmapSource, IsVisible: true } img
            && AutomationProperties.GetName(img) == "Слайд 4", "slide four on canvas");
        var handle = new WindowInteropHelper(window).Handle;
        var previousContext = SetThreadDpiAwarenessContext(-4);
        try
        {
            Assert.IsTrue(SystemParametersInfoW(48,0,out var work,0));
            int width = Math.Min(2400,work.Right - work.Left - 64), height = Math.Min(1550,work.Bottom - work.Top - 64);
            ShowWindow(handle,9);
            Assert.IsTrue(SetWindowPos(handle,-1,work.Left+32,work.Top+32,width,height,0x40));
            SetForegroundWindow(handle);
        }
        finally { if (previousContext != 0) SetThreadDpiAwarenessContext(previousContext); }
        await Task.Delay(250);
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        return (Desc<OfficeSlideObjectOverlay>(window,"Office.Native.SlideCanvas")!,
            Desc<Image>(window,"Office.Editor.Native.Preview.Image")!);
    }

    private static Rect SlideArea(FrameworkElement canvas, BitmapSource source)
    {
        var scale = Math.Min(canvas.ActualWidth / source.PixelWidth, canvas.ActualHeight / source.PixelHeight);
        var w = source.PixelWidth * scale; var h = source.PixelHeight * scale;
        return new Rect((canvas.ActualWidth - w) / 2, (canvas.ActualHeight - h) / 2, w, h);
    }

    private static bool HasMarkerAt(BitmapSource source, double x, double y)
    {
        var bitmap = new FormatConvertedBitmap(source,PixelFormats.Bgra32,null,0);
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect((int)(bitmap.PixelWidth*x),(int)(bitmap.PixelHeight*y),1,1),pixel,4,0);
        return Math.Abs(pixel[0]-193) < 15 && Math.Abs(pixel[1]-25) < 15 && Math.Abs(pixel[2]-251) < 15;
    }

    private static void WriteMarkerImage(string path)
    {
        var pixels = Enumerable.Range(0,60 * 43).SelectMany(_ => new byte[]{193,25,251,255}).ToArray();
        var source = BitmapSource.Create(60,43,96,96,PixelFormats.Bgra32,null,pixels,240);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(source));
        using var file = File.Create(path); png.Save(file);
    }

    private static T? Desc<T>(DependencyObject parent,string id) where T : DependencyObject
    {
        if (parent is T result && AutomationProperties.GetAutomationId(parent)==id) return result;
        for (var i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
            if (Desc<T>(VisualTreeHelper.GetChild(parent,i),id) is { } found) return found;
        return null;
    }

    private static async Task Wait(Func<bool> ready,string description)
    {
        var elapsed = Stopwatch.StartNew();
        while (!ready())
        {
            if (elapsed.Elapsed.TotalSeconds>80) Assert.Fail("Timed out waiting for "+description);
            await Task.Delay(70);
        }
    }

    private static void MouseButton(uint flags)
    {
        var input = new NativeInput { Mouse = new MouseInput { Flags = flags } };
        Assert.AreEqual(1u, SendInput(1, [input],Marshal.SizeOf<NativeInput>()));
    }

    private static async Task AimPointer(Window window, OfficeSlideObjectOverlay canvas, Point target)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var dpi = VisualTreeHelper.GetDpi(canvas);
        var initial = canvas.PointToScreen(target);
        var start = new PixelPoint { X=(int)Math.Round(initial.X),Y=(int)Math.Round(initial.Y) };
        if (GetAncestor(WindowFromPhysicalPoint(start),2) != handle)
        {
            // DWM exposes the visible frame in physical coordinates, independent of caller DPI.
            Assert.AreEqual(0,DwmGetWindowAttribute(handle,9,out var bounds,Marshal.SizeOf<PixelRect>()));
            start = new PixelPoint { X=(bounds.Left+bounds.Right)/2,Y=(bounds.Top+bounds.Bottom)/2 };
            Assert.AreEqual(handle,GetAncestor(WindowFromPhysicalPoint(start),2),"Dedicated test window must be visible.");
        }
        Assert.IsTrue(SetPhysicalCursorPos(start.X,start.Y));
        // Other WPF tests can initialize the shared STA on a different-DPI display.
        // Compare the physical desktop point with WPF's local point before injecting a button.
        for (var i = 0; i < 5; i++)
        {
            await Task.Delay(65);
            Assert.IsTrue(GetPhysicalCursorPos(out var physical));
            var error = target - Mouse.GetPosition(canvas);
            if (Math.Abs(error.X)*dpi.DpiScaleX <= 1.5 && Math.Abs(error.Y)*dpi.DpiScaleY <= 1.5)
            {
                Assert.AreEqual(handle,GetAncestor(WindowFromPhysicalPoint(physical),2),
                    "Only send pointer input to the dedicated test window.");
                Assert.AreSame(canvas,window.InputHitTest(canvas.TranslatePoint(target,window)));
                return;
            }
            var x = physical.X + (int)Math.Round(error.X*dpi.DpiScaleX);
            var y = physical.Y + (int)Math.Round(error.Y*dpi.DpiScaleY);
            Assert.IsTrue(SetPhysicalCursorPos(x,y));
        }
        Assert.Fail($"Pointer did not settle at {target}; WPF position {Mouse.GetPosition(canvas)}.");
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeInput { public uint Type; public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput
    {
        public int X, Y;
        public uint Data, Flags, Time;
        public nuint ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PixelRect { public int Left, Top, Right, Bottom; }
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window,uint key,out PixelRect rect,int size);
    [DllImport("user32.dll")] private static extern nint WindowFromPhysicalPoint(PixelPoint point);
    [DllImport("user32.dll")] private static extern bool GetPhysicalCursorPos(out PixelPoint p);
    [DllImport("user32.dll")] private static extern bool SetPhysicalCursorPos(int x,int y);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern bool SystemParametersInfoW(uint action,uint parameter,out PixelRect rect,uint flags);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window,nint after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window,int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint ancestor);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, NativeInput[] input, int size);
}
