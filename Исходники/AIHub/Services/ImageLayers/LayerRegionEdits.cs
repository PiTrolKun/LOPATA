using System.Drawing;
using System.IO;

namespace AIHub.Services.ImageLayers;

internal sealed partial class LayerDocument
{
    internal Dictionary<Guid, ImageLayer> PlanRegionEdit(Guid target, Rectangle area, string kind)
    {
        if (!_layers.TryGetValue(target, out var layer) || area.Width < 1 || area.Height < 1 || !Source.Frame.Contains(area))
            throw new InvalidDataException("ImageLayers.InvalidProject");

        var before = Aggregate(target);
        List<Point[]>? rings = null;
        var proposed = kind switch
        {
            IncludeRegionKind => before.Union(LayerMask.Area(Source.Width, Source.Height, area)),
            ExcludeRegionKind => before.Difference(LayerMask.Area(Source.Width, Source.Height, area)),
            RemoveRegionContoursKind => LayerContours.WithoutContoursInside(before, Source, area,
                rings = LayerContours.Trace(before, Source.Width)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var plan = PlanBoundary(target, proposed, allowEmpty: true);
        if (layer.Points is { } layout && layout.Silhouette == before.Key && layout.Silhouette != proposed.Key)
        {
            rings ??= LayerContours.Trace(before, Source.Width);
            var handles = LayerPointLayout.Resolve(before, layout.Step, rings, layout);
            var edit = LayerContourPoints.RetainPoints(proposed, Source, layout.Step, rings, handles, kind);
            plan[target] = plan[target] with { Points = edit.Points };
        }
        return plan;
    }
}
