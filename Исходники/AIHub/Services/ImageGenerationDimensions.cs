namespace AIHub.Services;

public static class ImageGenerationDimensions
{
    public static readonly (int Width, int Height)[] Ratios = [(1, 1), (4, 3), (3, 4), (16, 9), (9, 16), (3, 2), (2, 3)];
    public static (int Width, int Height) Fit(int ratioWidth, int ratioHeight, int longest, int maximum)
    {
        longest = Math.Clamp(longest / 64 * 64, 256, maximum);
        var unit = (double)longest / Math.Max(ratioWidth, ratioHeight);
        return (Math.Clamp((int)Math.Round(ratioWidth * unit / 64) * 64, 256, maximum),
            Math.Clamp((int)Math.Round(ratioHeight * unit / 64) * 64, 256, maximum));
    }
    public static string Ratio(int width, int height)
    {
        if (width <= 0 || height <= 0) return "1:1";
        foreach (var ratio in Ratios)
            if (Math.Abs((double)width / height - (double)ratio.Width / ratio.Height) < .025) return ratio.Width + ":" + ratio.Height;
        return width + ":" + height;
    }
    public static string Quality(int longest) => longest switch { 1024 => "1K", 1536 => "1.5K", 2048 => "2K", 4096 => "4K", _ => longest + "px" };
}
