using System.Globalization;
using System.IO;
using AIHub.Models;
using SkiaSharp;

namespace AIHub.Services;

internal sealed class ImageGenerationReferences : IDisposable
{
    private const int MaxPixels = 1024 * 1024;
    private string? _directory;

    private ImageGenerationReferences(string[] sources) => Paths = (string[])sources.Clone();

    internal string[] Paths { get; }

    internal static bool NeedsPreparation(ImageGenerationRequest request) =>
        request.ReferenceImages.Length > 1 && ImageGenerationCatalog.Get(request.ModelId).RequiresGpu;

    // Qwen otherwise sizes every VAE reference from the output canvas, including large canvases.
    internal static string NativeOptions(ImageGenerationRequest request) => NeedsPreparation(request)
        ? "vae_input_max_pixels=" + MaxPixels.ToString(CultureInfo.InvariantCulture)
        : "";

    internal static ImageGenerationReferences Prepare(string[] sources, CancellationToken token)
    {
        var references = new ImageGenerationReferences(sources);
        try
        {
            for (var index = 0; index < sources.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                var source = sources[index];
                using var codec = SKCodec.Create(source) ?? throw new InvalidDataException("Generation.InvalidImage");
                var (width, height) = (codec.Info.Width, codec.Info.Height);
                var area = (long)width * height;
                if (area <= MaxPixels) continue;

                // Resize the working copy before the engine decodes and duplicates the input pixels.
                var scale = Math.Sqrt((double)MaxPixels / area);
                references._directory ??= Directory.CreateTempSubdirectory("lopata-image-refs-").FullName;
                var output = Path.Combine(references._directory, index.ToString(CultureInfo.InvariantCulture) + ".png");
                ImageRasterScaler.SavePng(source, output, Math.Max(1, (int)(width * scale)),
                    Math.Max(1, (int)(height * scale)), new Dictionary<string, string>(), token);
                references.Paths[index] = output;
            }
            return references;
        }
        catch
        {
            references.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_directory is null) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(_directory)) File.Delete(file);
            Directory.Delete(_directory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
