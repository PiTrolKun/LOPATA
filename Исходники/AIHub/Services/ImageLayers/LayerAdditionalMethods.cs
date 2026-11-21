namespace AIHub.Services.ImageLayers;

internal enum LayerAdditionalMethod
{
    Matte = 1, Portrait = 2, Watershed = 3, Describe = 4, EdgeDetails = 5, Text = 6,
    SceneReview = 7, CompleteFrame = 8, RetiredQwen = 9
}

internal sealed record LayerMarker(int X, int Y, bool Foreground);

internal sealed record LayerEdgePoint(int X, int Y);
internal sealed record LayerEdgeStroke(bool Keep, LayerEdgePoint[] Points);

internal sealed record LayerAdditionalOptions(LayerAdditionalMethod Method, string? Description = null,
    LayerMarker[]? Markers = null, LayerEdgeStroke[]? Strokes = null);

internal static class LayerAdditionalMethods
{
    internal const string Matte = "soft-cutout";
    internal const string Portrait = "portrait-cutout";
    internal const string Watershed = "marker-selection";
    internal const string Describe = "description-selection";
    internal const string EdgeDetails = "edge-details";
    internal const string Text = "recognize-text";
    internal const string SceneReview = "review-scene";
    internal const string CompleteFrame = "complete-frame";
    internal const string RetiredQwen = "qwen-layered";

    internal static string Operation(this LayerAdditionalMethod method) => method switch
    {
        LayerAdditionalMethod.Matte => Matte,
        LayerAdditionalMethod.Portrait => Portrait,
        LayerAdditionalMethod.Watershed => Watershed,
        LayerAdditionalMethod.Describe => Describe,
        LayerAdditionalMethod.EdgeDetails => EdgeDetails,
        LayerAdditionalMethod.Text => Text,
        LayerAdditionalMethod.SceneReview => SceneReview,
        LayerAdditionalMethod.CompleteFrame => CompleteFrame,
        LayerAdditionalMethod.RetiredQwen => RetiredQwen,
        _ => throw new ArgumentOutOfRangeException(nameof(method))
    };

    internal static bool IsOperation(string kind) => kind is Matte or Portrait or Watershed or Describe or EdgeDetails
        or Text or SceneReview or CompleteFrame or RetiredQwen;

    internal static bool UsesScene(this LayerAdditionalOptions? extra) =>
        extra is null || extra.Method is LayerAdditionalMethod.SceneReview or LayerAdditionalMethod.CompleteFrame;
}
