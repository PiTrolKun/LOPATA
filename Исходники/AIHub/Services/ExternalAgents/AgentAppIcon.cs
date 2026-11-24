using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace AIHub.Services.ExternalAgents;

internal static class AgentAppIcon
{
    internal static ImageSource Load(AgentIdentity client, InstalledAgentApp? installed)
    {
        if (installed?.IconLocation is { } location)
        {
            if (Path.GetExtension(location).Equals(".png", StringComparison.OrdinalIgnoreCase) && File.Exists(location))
            {
                try { return LocalPng(location); }
                catch (Exception ex) when (ImageError(ex)) { }
            }
            try { if (ShellIcon(location) is { } shell) return shell; }
            catch (Exception ex) when (ImageError(ex)) { }
        }
        if (client.Icon is { Length: <= AgentIdentity.MaxIconChars } data && Inline(data) is { } icon) return icon;
        return UnidentifiedClientIcon;
    }

    // A separate monitor pictogram keeps an unidentified caller distinct from the connection button.
    private static readonly DrawingImage UnidentifiedClientIcon = CreateUnidentifiedClientIcon();

    private static DrawingImage CreateUnidentifiedClientIcon()
    {
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            var blue = new SolidColorBrush(Color.FromRgb(59, 106, 200));
            var outline = new Pen(Brushes.White, 2);
            dc.DrawRoundedRectangle(blue, null, new Rect(0, 0, 40, 40), 9, 9);
            dc.DrawRoundedRectangle(null, outline, new Rect(7, 9, 26, 18), 3, 3);
            dc.DrawLine(outline, new Point(20, 27), new Point(20, 31));
            dc.DrawLine(outline, new Point(14, 31), new Point(26, 31));
            foreach (var x in new[] { 14, 20, 26 })
                dc.DrawEllipse(Brushes.White, null, new Point(x, 18), 1.4, 1.4);
        }
        var icon = new DrawingImage(drawing);
        icon.Freeze();
        return icon;
    }

    private static BitmapImage LocalPng(string path)
    {
        using var stream = File.OpenRead(path);
        return Decode(stream);
    }

    private static BitmapSource? Inline(string data)
    {
        var png = data.StartsWith("data:image/png;base64,", StringComparison.OrdinalIgnoreCase);
        if (!png && !data.StartsWith("data:image/jpeg;base64,", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            using var stream = new MemoryStream(Convert.FromBase64String(data[(data.IndexOf(',') + 1)..]));
            BitmapDecoder header = png ? new PngBitmapDecoder(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None)
                : new JpegBitmapDecoder(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (header.Frames[0].PixelWidth is < 1 or > 1024 || header.Frames[0].PixelHeight is < 1 or > 1024) return null;
            stream.Position = 0;
            return Decode(stream);
        }
        catch (Exception ex) when (ImageError(ex)) { return null; }
    }

    private static bool ImageError(Exception ex) => ex is FormatException or IOException or NotSupportedException
        or InvalidOperationException or ArgumentException or UnauthorizedAccessException or COMException or OverflowException;

    private static BitmapImage Decode(Stream stream)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 96;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static BitmapSource? ShellIcon(string location)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(location, 0, ref iid, out var factory) < 0) return null;
        try
        {
            if (factory.GetImage(new NativeSize { Width = 96, Height = 96 }, 4, out var handle) < 0 || handle == 0) return null;
            try
            {
                var image = Imaging.CreateBitmapSourceFromHBitmap(handle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                image.Freeze();
                return image;
            }
            finally { DeleteObject(handle); }
        }
        finally { Marshal.ReleaseComObject(factory); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeSize { public int Width, Height; }
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(NativeSize size, uint flags, out nint bitmap);
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(string name, nint context, ref Guid iid, out IShellItemImageFactory factory);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint handle);
}
