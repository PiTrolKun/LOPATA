using System.Drawing;

namespace AIHub.Services.ImageLayers;

internal enum LayerLassoDestination { NewLayer, SelectedLayer, IntersectingLayer }
internal sealed record LayerLassoOptions(LayerLassoDestination Destination, bool Magnetic);
internal sealed record LayerLassoHit(Guid Id, string Name, int Pixels);

internal static class LayerLasso
{
    internal static LayerMask Select(LayerSource source, PointF[][] contours, CancellationToken token)
    {
        var selection = new LayerMask(source.Width * source.Height);
        foreach (var contour in contours)
        {
            token.ThrowIfCancellationRequested();
            selection = selection.Union(LayerContours.Fill(contour, source));
        }
        return selection.Intersect(source.VisibleMask(token));
    }

    internal static LayerLassoHit[] FindTargets(LayerDocument document, LayerMask selection, CancellationToken token)
    {
        var hits = new List<LayerLassoHit>();
        foreach (var layer in document.Layers.Values.Where(layer => layer.Mask is not null))
        {
            token.ThrowIfCancellationRequested();
            var overlap = document.Direct(layer).Intersect(selection);
            if (overlap.Any) hits.Add(new(layer.Id, LayerPath(layer), overlap.Count));
        }
        return hits.OrderByDescending(hit => hit.Pixels).ThenBy(hit => hit.Name, StringComparer.CurrentCulture).ToArray();

        string LayerPath(ImageLayer layer)
        {
            var path = new Stack<string>();
            for (ImageLayer? item = layer; item is not null;)
            {
                path.Push(item.Name);
                item = item.Parent is Guid id ? document.Layers[id] : null;
            }
            return string.Join(" › ", path);
        }
    }
}
