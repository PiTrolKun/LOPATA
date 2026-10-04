using System.Globalization;
using System.IO;
using AIHub.Models;

namespace AIHub.Services;

public static class ImageUtilityNaming
{
    public static string CreateStem(ImageUtilityItem item, ImageUtilityOptions options)
    {
        var original = Path.GetFileNameWithoutExtension(item.DisplayName);
        var suffix = options.FormatOnly ? "convert" : options.MethodId;
        return options.NamingMode switch
        {
            "original" => original,
            "method" => original + "_" + suffix,
            "series" => string.IsNullOrWhiteSpace(options.CustomName) ? "LOPATA" : options.CustomName.Trim(),
            "date" => DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture),
            _ => string.IsNullOrWhiteSpace(options.CustomName) ? original : options.CustomName.Trim()
        };
    }
}
