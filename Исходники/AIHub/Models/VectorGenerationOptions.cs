using System.IO;
using System.Text.RegularExpressions;

namespace AIHub.Models;

/// <summary>Immutable settings captured with each accepted request; legacy jobs have no options.</summary>
public sealed record VectorGenerationOptions(int CanvasWidth = 1, int CanvasHeight = 1,
    string Purpose = "auto", string Style = "auto", string Detail = "auto",
    string Palette = "auto", string Colors = "", string Background = "auto", string BackgroundColor = "#FFFFFF")
{
    public static readonly string[] Purposes = ["auto", "icon", "logo", "sticker", "illustration", "diagram", "poster"];
    public static readonly string[] Styles = ["auto", "outline", "flat", "gradient", "geometric", "handdrawn"];
    public static readonly string[] Details = ["auto", "simple", "normal", "detailed"];
    public static readonly string[] Palettes = ["auto", "mono", "custom"];
    public static readonly string[] Backgrounds = ["auto", "transparent", "light", "dark", "color", "scene"];
    public static bool IsColor(string? value) => value is not null && Regex.IsMatch(value, "^#[0-9a-fA-F]{6}$", RegexOptions.CultureInvariant);
    public string[] ColorList => (Colors ?? "").Split([' ', ',', ';', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries);
    public void Validate()
    {
        if (CanvasWidth <= 0 || CanvasHeight <= 0 || !Purposes.Contains(Purpose) || !Styles.Contains(Style)
            || !Details.Contains(Detail) || !Palettes.Contains(Palette) || !Backgrounds.Contains(Background)
            || Palette == "custom" && (ColorList.Length == 0 || ColorList.Any(c => !IsColor(c)))
            || Background == "color" && !IsColor(BackgroundColor)) throw new InvalidDataException("Vector.Error.Settings");
    }
}
