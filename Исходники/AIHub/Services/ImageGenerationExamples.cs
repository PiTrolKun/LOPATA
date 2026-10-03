using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;

namespace AIHub.Services;

public sealed record GenerationBlurRegion(double X, double Y, double Width, double Height);
public sealed record GenerationExample(string Id, string ModelId, string TitleKey, string Resource,
    string SourceLanguage, string PromptRu, string PromptEn, long Seed, string RequestId, string Sha256,
    GenerationBlurRegion[] BlurRegions)
{
    public string Prompt(string language) => language == "ru" ? PromptRu : PromptEn;
}

/// <summary>Original test assets; preview masking never modifies the embedded image.</summary>
public static class ImageGenerationExamples
{
    public static IReadOnlyList<GenerationExample> All { get; } = Load();
    private static GenerationExample[] Load()
    {
        using var stream = typeof(ImageGenerationExamples).Assembly.GetManifestResourceStream("AIHub.ImageGenerationExamples")
            ?? throw new InvalidOperationException("Missing generation examples.");
        return JsonSerializer.Deserialize<GenerationExample[]>(stream)!;
    }
    public static BitmapImage Image(GenerationExample example, int decodeWidth = 0)
    {
        using var stream = System.Windows.Application.GetResourceStream(new Uri(
            "/AIHub;component/Assets/GenerationExamples/" + example.Resource, UriKind.Relative))!.Stream;
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = decodeWidth; image.StreamSource = stream; image.EndInit(); image.Freeze();
        return image;
    }
}
