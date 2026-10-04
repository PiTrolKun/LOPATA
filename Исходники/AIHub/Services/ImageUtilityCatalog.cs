using AIHub.Models;

namespace AIHub.Services;

public static class ImageUtilityCatalog
{
    public static IReadOnlyList<ImageUtilityMethod> Methods { get; } = new[]
    {
        Method("real-esrgan", true), Method("real-cugan", true), Method("swinir", true),
        Method("lanczos3"), Method("catmull-rom"), Method("mitchell"), Method("hq2x"),
        Method("bilinear"), Method("nearest"), Method("area")
    };

    public static ImageUtilityMethod GetMethod(string id) => Methods.FirstOrDefault(x => x.Id == id)
        ?? throw new ImageUtilityException("ImageUtility.Error.Method");

    public static Dictionary<string, string> RecommendedParameters(string id) => id switch
    {
        "mitchell" => new() { ["b"] = "0.3333333333333333", ["c"] = "0.3333333333333333" },
        "catmull-rom" => new() { ["b"] = "0", ["c"] = "0.5" },
        "lanczos3" => new() { ["lobes"] = "3" },
        "nearest" => new() { ["integerScale"] = "0" },
        "hq2x" => new() { ["passes"] = "1" },
        "real-esrgan" => new() { ["scale"] = "4", ["tile"] = "0", ["device"] = "auto", ["tta"] = "false", ["model"] = "realesrgan-x4plus", ["threads"] = "1:2:2" },
        "real-cugan" => new() { ["scale"] = "2", ["tile"] = "0", ["device"] = "auto", ["tta"] = "false", ["noise"] = "-1", ["model"] = "models-se", ["threads"] = "1:2:2", ["syncgap"] = "3" },
        "swinir" => new() { ["scale"] = "4", ["tile"] = "256", ["overlap"] = "32", ["device"] = "auto" },
        _ => new()
    };

    private static ImageUtilityMethod Method(string id, bool ai = false) =>
        new(id, $"ImageUtility.Method.{id}.Name", $"ImageUtility.Method.{id}.Description", ai);
}
