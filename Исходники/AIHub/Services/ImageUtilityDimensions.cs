using AIHub.Models;

namespace AIHub.Services;

public static class ImageUtilityDimensions
{
    public static int LongEdge(int preset) => preset switch
    {
        360 => 640, 720 => 1280, 1080 => 1920, 2160 or 4 => 3840, 4320 or 8 => 7680,
        _ => throw new ImageUtilityException("ImageUtility.Error.Resolution")
    };

    public static (int Width, int Height) Calculate(int width, int height, int preset)
    {
        if (width < 1 || height < 1) throw new ImageUtilityException("ImageUtility.Error.Dimensions");
        var longest = LongEdge(preset);
        var scale = longest / (double)Math.Max(width, height);
        return (Math.Max(1, (int)Math.Round(width * scale, MidpointRounding.AwayFromZero)),
            Math.Max(1, (int)Math.Round(height * scale, MidpointRounding.AwayFromZero)));
    }

    public static (int Width, int Height) Calculate(int width, int height, ImageUtilityOptions options)
    {
        if (options.FormatOnly) return (width, height);
        if (options.ScaleMultiplier is { } multiplier)
        {
            if (multiplier is < 2 or > 16) throw new ImageUtilityException("ImageUtility.Error.Parameter");
            return (checked(width * multiplier), checked(height * multiplier));
        }
        if (options.MethodId == "nearest" && options.Parameters.TryGetValue("integerScale", out var value)
            && int.TryParse(value, out var integerScale) && integerScale > 0)
        {
            if (integerScale is < 2 or > 16) throw new ImageUtilityException("ImageUtility.Error.Parameter");
            return (checked(width * integerScale), checked(height * integerScale));
        }
        return Calculate(width, height, options.Preset);
    }
}
