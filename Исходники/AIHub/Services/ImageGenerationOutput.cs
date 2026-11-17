using System.IO;
using System.Text.Json;
using AIHub.Models;
using SkiaSharp;

namespace AIHub.Services;

/// <summary>Derived delivery file; the canonical model PNG remains the recovery checkpoint.</summary>
public static class ImageGenerationOutput
{
    public static string PathFor(ImageGenerationRequest request, int index) =>
        request.OutputLongestSide == 0 && request.OutputFormat == ImageGenerationFileFormat.Png
        ? ImageGenerationSessionStore.ResultPath(request, index)
        : Path.Combine(request.SessionDirectory, request.Id + "_" + index + ".output-" + request.OutputLongestSide + Extension(request.OutputFormat));

    internal static string FormatId(ImageGenerationFileFormat format) => format switch
    {
        ImageGenerationFileFormat.Png => "png",
        ImageGenerationFileFormat.Jpeg => "jpeg",
        ImageGenerationFileFormat.WebP => "webp",
        _ => throw new ArgumentException("Generation.InvalidInput", nameof(format))
    };

    internal static string Extension(ImageGenerationFileFormat format) => format == ImageGenerationFileFormat.Jpeg
        ? ".jpg" : "." + FormatId(format);

    public static bool IsReady(ImageGenerationRequest request, int index)
    {
        var size = ImageOutputDimensions.Fit(request.Width, request.Height, request.OutputLongestSide);
        var path = PathFor(request, index);
        if (request.OutputFormat == ImageGenerationFileFormat.Png)
            return ImageGenerationRuntime.IsValidImage(path, request with { Width = size.Width, Height = size.Height });
        if (!File.Exists(path)) return false;
        using var codec = SKCodec.Create(path);
        var encoding = request.OutputFormat == ImageGenerationFileFormat.Jpeg ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Webp;
        return codec is not null && codec.EncodedFormat == encoding && codec.Info.Width == size.Width && codec.Info.Height == size.Height;
    }

    public static ImageGenerationResult Prepare(ImageGenerationRequest request, ImageGenerationResult result, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            if (!ImageOutputDimensions.IsSupported(request.OutputLongestSide)) throw new ArgumentException("Generation.InvalidOutputSize");
            var source = ImageGenerationSessionStore.ResultPath(request, result.Index);
            if (!ImageGenerationRuntime.IsValidImage(source, request)) throw new InvalidDataException("Generation.InvalidImage");
            if (!IsReady(request, result.Index))
            {
                var size = ImageOutputDimensions.Fit(request.Width, request.Height, request.OutputLongestSide);
                var algorithm = ImageRasterScaler.Algorithm(request.Width, request.Height, size.Width, size.Height);
                var fields = ImageGenerationMetadata.ForOutput(PngTextMetadata.Read(source), request, size.Width, size.Height, algorithm);
                ImageRasterScaler.Save(source, PathFor(request, result.Index), size.Width, size.Height, request.OutputFormat, fields, token);
                if (!IsReady(request, result.Index)) throw new InvalidDataException("Generation.InvalidImage");
            }
            token.ThrowIfCancellationRequested();
            return ImageGenerationSessionStore.UpdateResult(request, result.Index, current => current with { ProcessingError = null });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or JsonException or System.Xml.XmlException)
        {
            return ImageGenerationSessionStore.UpdateResult(request, result.Index, current => current with
            { ProcessingError = error.Message, Exported = false });
        }
    }
}
