using System.IO;
using AIHub.Services.Books;
using AngleSharp.Dom;
using Markdig;

namespace AIHub.Services.OfficeReader;

internal static class ReaderMarkup
{
    // Opening a local document also permits its explicitly referenced local
    // pictures, including ../ paths. Never fetch web resources or run active HTML.
    internal static string Prepare(string path, CancellationToken token)
    {
        var text = ReaderOfficeExport.ReadText(path);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var html = extension is ".md" or ".markdown"
            ? Markdown.ToHtml(text, new MarkdownPipelineBuilder().UseAdvancedExtensions().Build()) : text;
        var document = BookMarkup.Parse(html); BookMarkup.Sanitize(document);
        foreach (var item in document.QuerySelectorAll("link,meta[http-equiv],audio,video,source")) item.Remove();
        var book = new BookContent { Title = Path.GetFileNameWithoutExtension(path) };
        foreach (var image in document.QuerySelectorAll("img"))
        {
            token.ThrowIfCancellationRequested();
            image.RemoveAttribute("srcset");
            var source = image.GetAttribute("src");
            if (string.IsNullOrWhiteSpace(source)) continue;
            if (source.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            { BookConversionResources.ReadDataResource(source, out _); continue; }
            if (Path.IsPathRooted(source) || Uri.TryCreate(source, UriKind.Absolute, out _))
            { image.Remove(); continue; }
            var local = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, Uri.UnescapeDataString(source.Split('#', '?')[0])));
            var bytes = BookConversionResources.LocalImage(local, Path.GetDirectoryName(local)!);
            var type = Path.GetExtension(local).ToLowerInvariant() switch
            { ".jpg" or ".jpeg" => "jpeg", ".tif" or ".tiff" => "tiff", var value => value.TrimStart('.') };
            image.SetAttribute("src", "data:image/" + type + ";base64," + Convert.ToBase64String(bytes));
        }
        BookConversionResources.SanitizeStyles(document, book);
        // Preserve source styles and language; no editing-model normalization.
        document.QuerySelectorAll("meta[charset]").ToList().ForEach(item => item.Remove());
        var charset = document.CreateElement("meta"); charset.SetAttribute("charset", "utf-8"); document.Head!.Prepend(charset);
        return "<!doctype html>" + document.DocumentElement!.OuterHtml;
    }
}
