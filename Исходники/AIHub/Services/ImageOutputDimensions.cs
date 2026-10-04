namespace AIHub.Services;

/// <summary>Delivery dimensions are independent of the model's multiple-of-64 input limits.</summary>
public static class ImageOutputDimensions
{
    public static IReadOnlyList<int> Presets { get; } = Array.AsReadOnly(new[] { 0, 1280, 1920, 3840 });
    public static bool IsSupported(int longestSide) => Presets.Contains(longestSide);
    public static int Normalize(int longestSide) => IsSupported(longestSide) ? longestSide : 0;
    public static string Label(int longestSide) => longestSide switch
    { 0 => "Generation.OutputOriginal", 1280 => "720p", 1920 => "1080p", 3840 => "4K", _ => throw new ArgumentOutOfRangeException(nameof(longestSide)) };

    public static (int Width, int Height) Fit(int width, int height, int longestSide)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (!IsSupported(longestSide)) throw new ArgumentOutOfRangeException(nameof(longestSide));
        if (longestSide == 0) return (width, height);
        var scale = (double)longestSide / Math.Max(width, height);
        return (Math.Max(1, (int)Math.Round(width * scale, MidpointRounding.AwayFromZero)),
            Math.Max(1, (int)Math.Round(height * scale, MidpointRounding.AwayFromZero)));
    }
}
