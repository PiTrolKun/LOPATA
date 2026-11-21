using System.Drawing;

namespace AIHub.Services.ImageLayers;

internal sealed partial class LayerDocument
{
    internal Dictionary<Guid, ImageLayer> PlanBoundary(Guid target, LayerMask proposed, bool allowEmpty = false)
    {
        var branch = Branch(target, _layers).ToHashSet();
        var before = Aggregate(target);
        if ((!allowEmpty && !proposed.Any) || !before.Changes(proposed).Any) return new(_layers);
        var additions = proposed.Difference(before);
        var released = before.Difference(proposed);
        var outside = _layers.Values.Where(layer => !branch.Contains(layer.Id) && layer.Mask is not null).ToArray();
        var transfers = DistributeReleased(released, before, outside);
        var result = new Dictionary<Guid, ImageLayer>(_layers);
        foreach (var item in outside)
        {
            var mask = Direct(item).Difference(additions);
            if (transfers.TryGetValue(item.Id, out var received)) mask = mask.Union(received);
            result[item.Id] = item with { Mask = Remember(mask) };
        }
        foreach (var id in branch)
        {
            var layer = _layers[id];
            var kept = Direct(layer).Difference(released);
            result[id] = layer with { Mask = Remember(id == target ? kept.Union(additions) : kept) };
        }
        return result;
    }

    private Dictionary<Guid, LayerMask> DistributeReleased(LayerMask released, LayerMask before, ImageLayer[] neighbors)
    {
        var width = Source.Width;
        var bounds = released.Bounds(width);
        if (bounds.IsEmpty || neighbors.Length == 0) return [];
        var search = Rectangle.Inflate(bounds, 1, 1);
        search.Intersect(Source.Frame);
        var masks = neighbors.Select(Direct).ToArray();
        var owners = new int[checked(search.Width * search.Height)];
        var pending = new Queue<Point>();
        for (var y = bounds.Top; y < bounds.Bottom; y++)
            for (var x = bounds.Left; x < bounds.Right; x++)
            {
                if (!released[y * width + x]) continue;
                var pixel = new Point(x, y);
                foreach (var fringe in Adjacent(pixel))
                {
                    if (!search.Contains(fringe) || before[fringe.Y * width + fringe.X]) continue;
                    var owner = Array.FindIndex(masks, mask => mask[fringe.Y * width + fringe.X]);
                    // Unassigned original pixels compete at equal distance with adjoining layers.
                    var key = owner < 0 ? -1 : owner + 1;
                    if (owners[Local(pixel)] <= 0) owners[Local(pixel)] = key;
                }
                if (owners[Local(pixel)] != 0) pending.Enqueue(pixel);
            }

        while (pending.TryDequeue(out var pixel))
            foreach (var next in Adjacent(pixel))
                if (bounds.Contains(next) && owners[Local(next)] == 0 && released[next.Y * width + next.X])
                {
                    owners[Local(next)] = owners[Local(pixel)];
                    pending.Enqueue(next);
                }

        return neighbors.Select((item, index) => (item.Id, index: index + 1))
            .Where(item => Array.IndexOf(owners, item.index) >= 0)
            .ToDictionary(item => item.Id, item => LayerMask.Build(Empty.Length, bits =>
            {
                for (var y = bounds.Top; y < bounds.Bottom; y++)
                    for (var x = bounds.Left; x < bounds.Right; x++)
                        if (owners[Local(new(x, y))] == item.index) bits[y * width + x] = true;
            }));

        int Local(Point point) => (point.Y - search.Top) * search.Width + point.X - search.Left;
    }

    private static IEnumerable<Point> Adjacent(Point point)
    {
        yield return new(point.X - 1, point.Y);
        yield return new(point.X + 1, point.Y);
        yield return new(point.X, point.Y - 1);
        yield return new(point.X, point.Y + 1);
    }

    internal Dictionary<Guid, ImageLayer> PlanRestore(Guid target, Guid? version)
    {
        var index = version is Guid id ? _history.FindIndex(step => step.Id == id)
            : _history.FindIndex(step => step.Changes.Any(change => change.Id == target)) - 1;
        if (version.HasValue && index < 0) throw new ArgumentException("Unknown layer history version.", nameof(version));
        var snapshot = At(index);
        var members = Branch(target, _layers).Concat(Branch(target, snapshot)).ToHashSet();
        var previous = Aggregate(target, snapshot);
        var current = Aggregate(target);
        var gain = previous.Difference(current);
        var loss = current.Difference(previous);
        var protectedPixels = Empty;
        foreach (var step in _history.Skip(index + 1))
        {
            // Preserve pixels affected by later edits on an unrelated layer, even inside the restored region.
            if (step.Changes.Any(change => members.Contains(change.Id))) continue;
            foreach (var change in step.Changes)
                if (change.Before?.Mask != change.After.Mask)
                    protectedPixels = protectedPixels.Union(Mask(change.Before?.Mask).Changes(Direct(change.After)));
        }
        var result = new Dictionary<Guid, ImageLayer>(_layers);
        var unavailable = Empty;
        foreach (var layer in _layers.Values.Where(layer => !members.Contains(layer.Id)))
        {
            var mask = Direct(layer).Difference(gain.Difference(protectedPixels));
            if (snapshot.TryGetValue(layer.Id, out var sibling))
                mask = mask.Union(Direct(sibling).Intersect(loss).Difference(protectedPixels));
            result[layer.Id] = layer with { Mask = Remember(mask) };
            unavailable = unavailable.Union(mask);
        }
        foreach (var member in members)
        {
            var metadata = snapshot.GetValueOrDefault(member) ?? _layers[member];
            var desired = Mask(snapshot.GetValueOrDefault(member)?.Mask).Difference(unavailable)
                .Difference(protectedPixels.Intersect(gain));
            var original = snapshot.GetValueOrDefault(member);
            result[member] = metadata with { Mask = Remember(desired), Points = original?.Points, Matte = original?.Matte };
        }
        return result;
    }
}
