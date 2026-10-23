using System.Globalization;
using System.IO;
using System.Xml.Linq;
using AIHub.Models;
using SkiaSharp;

namespace AIHub.Services.VectorGraphics;

public sealed record VectorRasterOptions(int Width, int Height, string Format = "png", int Quality = 95,
    string Background = "transparent", string BackgroundColor = "#FFFFFF")
{
    public void Validate()
    {
        if (Width <= 0 || Height <= 0 || Format is not ("png" or "jpeg" or "webp") || Quality is < 1 or > 100
            || Background is not ("transparent" or "color") || !VectorGenerationOptions.IsColor(BackgroundColor))
            throw new InvalidDataException("Vector.Error.ExportSettings");
        // Codec format constraints, rather than a list of application resolution presets.
        if (Format == "jpeg" && (Width > 65500 || Height > 65500)
            || Format == "webp" && (Width > 16383 || Height > 16383)) throw new InvalidDataException("Vector.Error.FormatDimensions");
    }
}

/// <summary>CPU export from the original SVG; temporary files are committed only after encoding and metadata succeed.</summary>
public static class VectorRasterExport
{
    public static long EstimatedBytes(VectorRasterOptions options)
    {
        options.Validate();
        try { return checked((long)options.Width * options.Height * 24 + 32L * 1024 * 1024); }
        catch (OverflowException) { throw new InvalidDataException("Vector.Error.ExportMemory"); }
    }
    public static async Task SaveAsync(VectorResult item, string destination, VectorRasterOptions options, CancellationToken token)
    {
        using var audit = ComponentAudit.CaptureOperation(); options.Validate(); token.ThrowIfCancellationRequested();
        var needed = EstimatedBytes(options);
        if (!new ImageAnalysisSpeechMemoryPolicy().EvaluateCurrent(needed, 0).HasEnoughMemory)
            throw new InvalidDataException("Vector.Error.ExportMemory");
        var source = VectorStore.FilePath(item.Request.Id, "image.svg");
        var path = Path.GetFullPath(destination);
        Lopata.Updates.SafeUpdatePath.RejectLinks(path);
        // An export cannot overwrite any original, preview, checkpoint or other file of this job.
        var job = Path.GetFullPath(VectorStore.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (path.StartsWith(job, StringComparison.OrdinalIgnoreCase)
            || !(options.Format == "jpeg" ? extension is ".jpeg" or ".jpg" : extension == "." + options.Format)
            || item.ExportPath is { } original && path.Equals(Path.GetFullPath(original), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Vector.Error.ExportDestination");
        Lopata.Updates.SafeUpdatePath.RejectLinks(source);
        var svg = await File.ReadAllTextAsync(source, token);
        var composed = Compose(svg, options);
        var directory = Path.GetDirectoryName(path)!; Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".export.tmp");
        var rendered = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".export.png");
        var facts = (item.Provenance ?? new OutputProvenance { ModelHistory = "unknown", CreatedUtc = item.Request.CreatedUtc }) with
        { Scenario = "vector-graphics/raster-export", ExportedUtc = DateTimeOffset.UtcNow,
            ProgramVersion = new OutputProvenance().ProgramVersion };
        try
        {
            await VectorRenderer.RenderFrameAsync(composed, rendered, options.Width, options.Height, token);
            await Task.Run(() => Encode(rendered, temporary, options, facts, token), token);
            token.ThrowIfCancellationRequested(); File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            if (File.Exists(rendered)) File.Delete(rendered);
        }
    }
    internal static string Compose(string svg, VectorRasterOptions options)
    {
        options.Validate();
        var root = VectorSvg.Parse(svg).Root!;
        var box = root.Attribute("viewBox")!.Value.Split([' ', ',', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(n => double.Parse(n, CultureInfo.InvariantCulture)).ToArray();
        var frame = VectorSvg.Parse(VectorViewportFrame.Compose(svg, options.Width, options.Height, box[2] / box[3], 1, 0, 0));
        if (options.Background == "color" || options.Format == "jpeg")
        {
            XNamespace ns = "http://www.w3.org/2000/svg";
            frame.Root!.AddFirst(new XElement(ns + "rect", new XAttribute("width", options.Width),
                new XAttribute("height", options.Height), new XAttribute("fill", options.Background == "color" ? options.BackgroundColor : "#FFFFFF")));
        }
        return frame.ToString(SaveOptions.DisableFormatting);
    }
    private static void Encode(string source, string destination, VectorRasterOptions options, OutputProvenance facts, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var codec = SKCodec.Create(source) ?? throw new InvalidDataException("Vector.Error.Render");
        if (codec.Info.Width != options.Width || codec.Info.Height != options.Height) throw new InvalidDataException("Vector.Error.Render");
        if (options.Format == "png") File.Copy(source, destination, false);
        else
        {
            using var bitmap = SKBitmap.Decode(codec) ?? throw new InvalidDataException("Vector.Error.Render");
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(options.Format == "jpeg" ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Webp, options.Quality)
                ?? throw new InvalidDataException("Vector.Error.Encode");
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            encoded.SaveTo(output); output.Flush(true);
        }
        token.ThrowIfCancellationRequested();
        OutputImageMetadata.Write(destination, options.Format, options.Width, options.Height, facts);
    }
}
