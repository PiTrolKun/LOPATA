using System.Globalization;
using AIHub.Models;

namespace AIHub.Services.VectorGraphics;

public static class VectorPrompt
{
    public static string Compose(string prompt, VectorGenerationOptions? options)
    {
        if (options is null) return prompt; // Keep old saved requests semantically unchanged.
        options.Validate();
        return prompt + "\n\nDrawing settings:\nCanvas aspect ratio: "
            + options.CanvasWidth.ToString(CultureInfo.InvariantCulture) + ":" + options.CanvasHeight.ToString(CultureInfo.InvariantCulture)
            + ". Choose a matching viewBox and compose the drawing for that canvas.\n"
            + "Purpose: " + Purpose(options.Purpose) + ".\nStyle: " + Style(options.Style)
            + ".\nDetail: " + Detail(options.Detail) + ".\nPalette: "
            + (options.Palette == "custom" ? "use these main colors: " + string.Join(", ", options.ColorList)
                : options.Palette == "mono" ? "monochrome; shades of a single hue" : "choose colors to suit the user's request")
            + ".\nBackground: " + Background(options) + ".";
    }
    private static string Purpose(string id) => id switch
    {
        "icon" => "a clearly readable icon, recognizable at small display sizes",
        "logo" => "a distinctive logo with a clear silhouette",
        "sticker" => "a sticker with a coherent standalone silhouette",
        "illustration" => "a finished vector illustration", "diagram" => "a clear diagram with readable labels",
        "poster" => "a poster with a clear visual hierarchy", _ => "follow the user's request"
    };
    private static string Style(string id) => id switch
    {
        "outline" => "outline / line art", "flat" => "flat vector art with solid fills",
        "gradient" => "vector illustration with gradients", "geometric" => "geometric vector art",
        "handdrawn" => "hand-drawn looking vector lines", _ => "follow the user's request"
    };
    private static string Detail(string id) => id switch
    {
        "simple" => "concise shapes and only essential details", "normal" => "balanced detail",
        "detailed" => "rich and meaningful detail; finish the entire illustration", _ => "follow the user's request"
    };
    private static string Background(VectorGenerationOptions o) => o.Background switch
    {
        "transparent" => "transparent canvas, no full-canvas background shapes",
        "light" => "solid white", "dark" => "solid black", "color" => "solid " + o.BackgroundColor,
        "scene" => "draw the background as part of the scene", _ => "follow the user's request"
    };
}
