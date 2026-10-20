using System.IO;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using SkiaSharp;

namespace AIHub.Services.Books;

internal static class BookConversionResources
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff" };
    public static byte[] LocalImage(string path, string root)
    {
        if (!ImageExtensions.Contains(Path.GetExtension(path))) throw new InvalidDataException("BookConverter.Error.ImageFormat");
        var current = new FileInfo(path) as FileSystemInfo;
        while (current is not null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("BookConverter.Error.ImageReparse");
            current = current switch { FileInfo f => f.Directory, DirectoryInfo d => d.Parent, _ => null };
        }
        if (new FileInfo(path).Length > 128L * 1024 * 1024) throw new InvalidDataException("BookConverter.Error.ImageBytes");
        var bytes = File.ReadAllBytes(path); ValidateImage(bytes); return bytes;
    }
    public static void ValidateImage(byte[] data)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(data));
        if (codec is null || codec.Info.Width < 1 || codec.Info.Height < 1) throw new InvalidDataException("BookConverter.Error.InvalidImage");
        if ((long)codec.Info.Width * codec.Info.Height > 100_000_000) throw new InvalidDataException("BookConverter.Error.ImagePixels");
    }
    public static byte[] ReadDataResource(string uri, out string mediaType)
    {
        var comma = uri.IndexOf(',');
        if (!uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || comma < 0 || !uri[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("BookConverter.Error.InvalidImage");
        mediaType = uri[5..(comma - 7)].ToLowerInvariant();
        if (uri.Length - comma > 180_000_000) throw new InvalidDataException("BookConverter.Error.ImageBytes");
        var bytes = Convert.FromBase64String(uri[(comma + 1)..]);
        if (bytes.Length > 128L * 1024 * 1024) throw new InvalidDataException("BookConverter.Error.ImageBytes");
        if (mediaType == "image/svg+xml") ValidateSvg(bytes);
        else if (mediaType is "image/png" or "image/jpeg" or "image/gif" or "image/bmp" or "image/webp" or "image/tiff") ValidateImage(bytes);
        else if (mediaType is "font/woff" or "font/woff2" or "font/ttf" or "font/otf")
        {
            var signature = bytes.Length >= 4 ? System.Text.Encoding.ASCII.GetString(bytes, 0, 4) : "";
            if (!(signature is "wOFF" or "wOF2" or "OTTO" or "true") && !(bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 1 && bytes[2] == 0 && bytes[3] == 0)) throw new InvalidDataException("BookConverter.Error.InvalidImage");
        }
        else throw new InvalidDataException("BookConverter.Error.InvalidImage");
        return bytes;
    }
    public static void ValidateSvg(byte[] data, IReadOnlySet<string>? allowedResources = null)
    {
        using var stream = new MemoryStream(data); var document = BookMarkup.Xml(stream);
        if (document.Root?.Name.LocalName != "svg") throw new InvalidDataException("BookConverter.Error.InvalidImage");
        if (document.DescendantNodes().OfType<System.Xml.Linq.XProcessingInstruction>().Any()) throw new InvalidDataException("BookConverter.Error.ExternalResource");
        void CheckCss(string css)
        {
            if (css.Contains('\\') || css.Contains("@import", StringComparison.OrdinalIgnoreCase) || css.Contains("expression(", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("BookConverter.Error.ExternalResource");
            foreach (Match match in Regex.Matches(css, @"url\(\s*['"" ]?(?<url>[^)'""]+)['"" ]?\s*\)", RegexOptions.IgnoreCase))
                if (!match.Groups["url"].Value.Trim().StartsWith('#') && allowedResources?.Contains(match.Groups["url"].Value.Trim()) != true) throw new InvalidDataException("BookConverter.Error.ExternalResource");
        }
        foreach (var element in document.Descendants())
        {
            if (element.Name.LocalName is "script" or "foreignObject" or "animate" or "animateTransform" or "animateMotion" or "set") throw new InvalidDataException("BookConverter.Error.ExternalResource");
            if (element.Name.LocalName == "style") CheckCss(element.Value);
            foreach (var attribute in element.Attributes())
            {
                if (attribute.Name == System.Xml.Linq.XNamespace.Xml + "base") throw new InvalidDataException("BookConverter.Error.ExternalResource");
                if (attribute.Name.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("BookConverter.Error.ExternalResource");
                if (attribute.Name.LocalName is "href" or "src" && attribute.Value.Length > 0 && !attribute.Value.StartsWith('#') && allowedResources?.Contains(attribute.Value) != true) throw new InvalidDataException("BookConverter.Error.ExternalResource");
                if (attribute.Name.LocalName == "style" || attribute.Value.Contains('\\') || attribute.Value.Contains("url(", StringComparison.OrdinalIgnoreCase)) CheckCss(attribute.Value);
            }
        }
    }
    public static void SanitizeStyles(IDocument document, BookContent book)
    {
        string Clean(string css)
        {
            if (css.Contains('\\') || css.Contains("@import", StringComparison.OrdinalIgnoreCase) || css.Contains("expression(", StringComparison.OrdinalIgnoreCase))
            { book.Warnings.Add("BookConverter.Warning.UnsafeCss"); return ""; }
            return Regex.Replace(css, @"url\(\s*['"" ]?(?<url>[^)'""]+)['"" ]?\s*\)", m =>
            {
                var url = m.Groups["url"].Value.Trim();
                if (book.Assets.ContainsKey(url)) return m.Value;
                if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) { ReadDataResource(url, out _); return m.Value; }
                book.Warnings.Add("BookConverter.Warning.ExternalCss");
                return "none";
            }, RegexOptions.IgnoreCase);
        }
        foreach (var style in document.QuerySelectorAll("style")) style.TextContent = Clean(style.TextContent);
        foreach (var element in document.All) if (element.GetAttribute("style") is { } css) element.SetAttribute("style", Clean(css));
    }
}
