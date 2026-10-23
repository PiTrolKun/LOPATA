using System.IO;
using System.Text;
using System.Windows.Media.Imaging;

namespace AIHub.Services.VectorGraphics;

public sealed record VectorExample(string Id);

/// <summary>Preserved Ridge test results, never regenerated or rewritten for the gallery.</summary>
public static class VectorExamples
{
    public static IReadOnlyList<VectorExample> All { get; } = Array.AsReadOnly(new[] { new VectorExample("matrix-cat"), new VectorExample("workshop") });
    public static Stream Open(string name) => typeof(VectorExamples).Assembly.GetManifestResourceStream("AIHub.VectorExamples." + name)
        ?? throw new FileNotFoundException("Missing SVG example: " + name);
    public static string Prompt(VectorExample example)
    {
        using var input = Open(example.Id + ".prompt.txt"); using var reader = new StreamReader(input, Encoding.UTF8);
        return reader.ReadToEnd();
    }
    public static string Svg(VectorExample example)
    {
        using var input = Open(example.Id + ".svg"); using var reader = new StreamReader(input, Encoding.UTF8);
        return reader.ReadToEnd();
    }
    public static BitmapImage Image(VectorExample example, int width = 0)
    {
        using var stream = Open(example.Id + ".png");
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = width; image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
    }
}
