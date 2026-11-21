using System.Drawing;

namespace AIHub.Services.ImageLayers;

internal sealed record LayerReviewAnchor(string Name, int[] Box);

internal sealed record LayerSceneReview(string[] Missing, string[] Split, string[] Merged, string[] Boundary)
{
    private sealed record Shape(string Name, LayerMask Mask, Rectangle Bounds);

    internal static LayerReviewAnchor[] Inventory(LayerDocument current, CancellationToken token)
    {
        var result = new List<LayerReviewAnchor>();
        var source = current.Source;
        foreach (var layer in SceneRoots(current).Take(LayerAnalysisProtocol.Limit(LayerDepth.Details)))
        {
            token.ThrowIfCancellationRequested();
            var bounds = current.Aggregate(layer.Id).Bounds(source.Width);
            if (bounds.IsEmpty) continue;
            result.Add(new(layer.Name,
                [(int)Math.Floor(bounds.Left * 1000d / source.Width), (int)Math.Floor(bounds.Top * 1000d / source.Height),
                 (int)Math.Ceiling(bounds.Right * 1000d / source.Width), (int)Math.Ceiling(bounds.Bottom * 1000d / source.Height)]));
        }
        return [.. result];
    }

    internal static LayerProposal WithoutBackground(LayerProposal proposal)
    {
        var byId = proposal.Layers.ToDictionary(layer => layer.Id);
        return proposal with { Layers = proposal.Layers.Where(layer => !IsBackgroundBranch(layer)).ToArray() };

        bool IsBackgroundBranch(LayerCandidate layer)
        {
            while (layer.Parent is { } parent) layer = byId[parent];
            return layer.Text is null && Background(layer.Name);
        }
    }

    internal static LayerSceneReview Compare(LayerDocument current, LayerProposal suggestion, CancellationToken token)
    {
        var width = current.Source.Width;
        var existing = SceneRoots(current)
            .Select(layer => Describe(layer.Name, current.Aggregate(layer.Id))).Where(shape => !shape.Bounds.IsEmpty).ToArray();

        var byId = suggestion.Layers.ToDictionary(layer => layer.Id);
        var found = suggestion.Layers.GroupBy(Root, layer => layer.Mask)
            .Where(group => group.Key.Text is null && !Background(group.Key.Name))
            .Select(group => Describe(group.Key.Name, current.Merge(group))).Where(shape => !shape.Bounds.IsEmpty).ToArray();
        var intersections = new int[found.Length, existing.Length];
        for (var next = 0; next < found.Length; next++)
            for (var old = 0; old < existing.Length; old++)
            {
                token.ThrowIfCancellationRequested();
                if (found[next].Bounds.IntersectsWith(existing[old].Bounds))
                    intersections[next, old] = found[next].Mask.Intersect(existing[old].Mask).Count;
            }

        List<string> missing = [], split = [], merged = [], boundary = [];
        for (var next = 0; next < found.Length; next++)
        {
            var shape = found[next];
            var overlap = Enumerable.Range(0, existing.Length).Where(old => intersections[next, old] > 0)
                .OrderByDescending(old => intersections[next, old]).ToArray();
            if (overlap.Length == 0 || overlap.Sum(old => (long)intersections[next, old]) < .35 * shape.Mask.Count)
            {
                missing.Add(shape.Name);
                continue;
            }

            if (overlap.Count(old => intersections[next, old] >= .55 * existing[old].Mask.Count
                && intersections[next, old] >= .12 * shape.Mask.Count) > 1)
                split.Add(shape.Name);
            else
            {
                var best = overlap[0];
                var common = intersections[next, best];
                if (common < .8 * (shape.Mask.Count + existing[best].Mask.Count - common))
                    boundary.Add(shape.Name);
            }
        }
        for (var old = 0; old < existing.Length; old++)
            if (Enumerable.Range(0, found.Length).Count(next => intersections[next, old] > 0
                && intersections[next, old] >= .55 * found[next].Mask.Count
                && intersections[next, old] >= .12 * existing[old].Mask.Count) > 1)
                merged.Add(existing[old].Name);

        return new([.. missing], [.. split], [.. merged], [.. boundary]);

        Shape Describe(string name, LayerMask mask) => new(name, mask, mask.Any ? mask.Bounds(width) : Rectangle.Empty);

        LayerCandidate Root(LayerCandidate layer)
        {
            while (layer.Parent is { } parent) layer = byId[parent];
            return layer;
        }
    }

    internal static bool Background(string name) => name.Equals("background", StringComparison.OrdinalIgnoreCase)
        || name.Equals("фон", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<ImageLayer> SceneRoots(LayerDocument current)
    {
        var background = current.FrameBackgroundId;
        return current.Layers.Values.Where(layer => layer.Parent is null && layer.Id != background
            && layer.Text is null && !Background(layer.Name));
    }
}
