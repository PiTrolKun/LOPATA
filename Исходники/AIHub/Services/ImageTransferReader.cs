using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using WpfData = System.Windows.IDataObject;
using Formats = System.Windows.DataFormats;

namespace AIHub.Services;

/// <summary>Snapshots explicit paste/drop data while its source still owns the transfer.</summary>
public static class ImageTransferReader
{
    public static bool MayContainImage(WpfData data) => data.GetDataPresent(Formats.FileDrop)
        || data.GetDataPresent("PNG") || data.GetDataPresent(Formats.Bitmap)
        || data.GetDataPresent("text/uri-list") || data.GetDataPresent(Formats.UnicodeText) || data.GetDataPresent(Formats.Html);

    public static ImageInput Read(WpfData data)
    {
        if (data.GetDataPresent(Formats.FileDrop) && data.GetData(Formats.FileDrop) is string[] files)
        {
            if (files.Length != 1) throw new ImageInputException("ImageInput.SingleOnly");
            return new(FilePath: files[0]);
        }
        if (data.GetDataPresent("PNG"))
        {
            var png = data.GetData("PNG");
            if (png is byte[] bytes) return new(Png: CheckedBytes(bytes));
            if (png is Stream stream)
            {
                if (stream.CanSeek) stream.Position = 0;
                using var copy = new MemoryStream();
                var buffer = new byte[81920]; int read;
                while ((read = stream.Read(buffer)) > 0)
                { if (copy.Length + read > ImageInputService.MaxBytes) throw new ImageInputException("ImageInput.TooLarge"); copy.Write(buffer, 0, read); }
                return new(Png: copy.ToArray());
            }
        }
        if (data.GetDataPresent(Formats.Bitmap))
        {
            var value = data.GetData(Formats.Bitmap);
            if (value is BitmapSource bitmap)
            {
                if ((long)bitmap.PixelWidth * bitmap.PixelHeight > ImageInputService.MaxPixels) throw new ImageInputException("ImageInput.TooLarge");
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = new MemoryStream(); encoder.Save(stream);
                return new(Png: CheckedBytes(stream.ToArray()));
            }
            if (value is System.Drawing.Bitmap drawing)
            {
                if ((long)drawing.Width * drawing.Height > ImageInputService.MaxPixels) throw new ImageInputException("ImageInput.TooLarge");
                using var stream = new MemoryStream(); drawing.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                return new(Png: CheckedBytes(stream.ToArray()));
            }
        }
        // A browser may supply just an HTML fragment for a dragged image. Never execute or fetch the page.
        if (data.GetDataPresent(Formats.Html) && data.GetData(Formats.Html) is string html && html.Length <= 1024 * 1024)
        {
            var images = Regex.Matches(html, @"<img\b[^>]*\bsrc\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
            if (images.Count > 1) throw new ImageInputException("ImageInput.SingleOnly");
            if (images.Count == 1 && Uri.TryCreate(WebUtility.HtmlDecode(images[0].Groups[1].Value), UriKind.Absolute, out var uri)
                && ImageInputService.IsHttp(uri)) return new(Url: uri);
        }
        foreach (var format in new[] { "text/uri-list", Formats.UnicodeText, Formats.Text })
        {
            if (!data.GetDataPresent(format) || data.GetData(format) is not string text) continue;
            var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => !l.StartsWith('#')).ToArray();
            if (lines.Length > 1) throw new ImageInputException("ImageInput.SingleOnly");
            if (lines.Length == 1 && Uri.TryCreate(lines[0], UriKind.Absolute, out var uri) && ImageInputService.IsHttp(uri))
                return new(Url: uri);
        }
        throw new ImageInputException("ImageInput.Unsupported");
    }

    private static byte[] CheckedBytes(byte[] bytes)
    {
        if (bytes.Length > ImageInputService.MaxBytes) throw new ImageInputException("ImageInput.TooLarge");
        return bytes.ToArray();
    }
}
