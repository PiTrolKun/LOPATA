using System.Buffers.Text;
using System.Collections;
using System.Drawing;

namespace AIHub.Services.ImageLayers;

internal readonly record struct LayerControlPoint(int Ring, int Vertex);

// Each bit refers to one vertex of the traced, unchanged mask. Record equality is used when reopening history.
internal sealed record LayerPointLayout(string Silhouette, int Step, string Vertices)
{
    internal static readonly int[] Steps = [1, 2, 4, 8, 16, 32, 64, 128];

    internal bool IsValid() => Silhouette is { Length: 64 } && Silhouette.All(Uri.IsHexDigit)
        && Steps.Contains(Step) && !string.IsNullOrEmpty(Vertices) && Base64.IsValid(Vertices);

    internal static LayerPointLayout Encode(LayerMask mask, int step, IReadOnlyList<Point[]> rings, int[][] handles)
    {
        var bits = new BitArray(rings.Sum(ring => ring.Length));
        var offset = 0;
        for (var i = 0; i < rings.Count; i++)
        {
            foreach (var vertex in handles[i]) bits[offset + vertex] = true;
            offset += rings[i].Length;
        }
        var bytes = new byte[(bits.Length + 7) / 8];
        bits.CopyTo(bytes, 0);
        return new(mask.Key, step, Convert.ToBase64String(bytes));
    }

    internal static int[][] Resolve(LayerMask mask, int step, IReadOnlyList<Point[]> rings, LayerPointLayout? layout)
    {
        if (layout is null || layout.Step != step || layout.Silhouette != mask.Key || !layout.IsValid())
            return rings.Select(ring => LayerContours.Handles(ring, step)).ToArray();

        var bits = new BitArray(Convert.FromBase64String(layout.Vertices));
        var result = new int[rings.Count][];
        var offset = 0;
        for (var i = 0; i < rings.Count; i++)
        {
            var length = rings[i].Length;
            var points = offset + length <= bits.Length
                ? Enumerable.Range(0, length).Where(vertex => bits[offset + vertex]).ToArray() : [];
            result[i] = points.Length >= 3 ? points : LayerContours.Handles(rings[i], step);
            offset += length;
        }
        return result;
    }
}

internal sealed record LayerContourEdit(LayerMask Mask, string Kind, LayerPointLayout? Points = null,
    LayerControlPoint[]? Selection = null);

internal static class LayerContourPoints
{
    internal static LayerContourEdit Insert(LayerMask mask, int step, IReadOnlyList<Point[]> rings, int[][] handles,
        LayerControlPoint point)
    {
        var next = (int[][])handles.Clone();
        next[point.Ring] = handles[point.Ring].Append(point.Vertex).Distinct().Order().ToArray();
        return new(mask, LayerDocument.AddPointKind, LayerPointLayout.Encode(mask, step, rings, next), [point]);
    }

    internal static LayerContourEdit? Move(LayerMask mask, LayerSource source, int step, IReadOnlyList<Point[]> rings,
        int[][] handles, IReadOnlyDictionary<LayerControlPoint, PointF> positions, string kind)
    {
        var updated = mask;
        foreach (var moves in positions.GroupBy(entry => entry.Key.Ring))
            updated = LayerContours.Warp(updated, source, rings[moves.Key], handles[moves.Key],
                moves.ToDictionary(entry => entry.Key.Vertex, entry => entry.Value));
        if (!updated.Any || updated.Key == mask.Key) return null;
        return RetainPoints(updated, source, step, rings, handles, kind, positions);
    }

    internal static LayerContourEdit Remove(LayerMask mask, LayerSource source, int step, IReadOnlyList<Point[]> rings,
        int[][] handles, IReadOnlySet<LayerControlPoint> selection)
    {
        var kept = (int[][])handles.Clone();
        var updated = mask;
        var erased = new List<Point[]>();
        foreach (var points in selection.GroupBy(point => point.Ring))
        {
            var ring = points.Key;
            var vertices = points.Select(point => point.Vertex).ToHashSet();
            kept[ring] = handles[ring].Where(vertex => !vertices.Contains(vertex)).ToArray();
            if (kept[ring].Length >= 3)
                updated = LayerContours.Reconnect(updated, source, rings[ring], handles[ring], kept[ring]);
            else
            {
                kept[ring] = [];
                erased.Add(rings[ring]);
            }
        }
        foreach (var ring in erased.Where(LayerContours.IsHole))
            updated = updated.Union(LayerContours.RingArea(ring, source));
        foreach (var ring in erased.Where(ring => !LayerContours.IsHole(ring)))
            updated = updated.Difference(LayerContours.RingArea(ring, source));
        return RetainPoints(updated, source, step, rings, kept, LayerDocument.RemovePointsKind);
    }

    internal static LayerContourEdit RetainPoints(LayerMask mask, LayerSource source, int step, IReadOnlyList<Point[]> oldRings,
        int[][] oldHandles, string kind, IReadOnlyDictionary<LayerControlPoint, PointF>? moved = null)
    {
        if (!mask.Any) return new(mask, kind);
        var rings = LayerContours.Trace(mask, source.Width);
        var locations = new Dictionary<Point, LayerControlPoint>();
        var handles = new HashSet<int>[rings.Count];
        var selected = new HashSet<LayerControlPoint>();
        for (var ring = 0; ring < rings.Count; ring++)
        {
            handles[ring] = [];
            for (var vertex = 0; vertex < rings[ring].Length; vertex++)
                locations.TryAdd(rings[ring][vertex], new(ring, vertex));
        }

        for (var ring = 0; ring < oldRings.Count; ring++)
            foreach (var index in oldHandles[ring])
            {
                var candidate = (PointF)oldRings[ring][index];
                var wasMoved = moved is not null && moved.TryGetValue(new(ring, index), out candidate);
                if (!wasMoved) candidate = oldRings[ring][index];
                if (Find(candidate) is not { } point) continue;
                handles[point.Ring].Add(point.Vertex);
                if (wasMoved && kind == LayerDocument.PointsKind) selected.Add(point);
            }

        var result = handles.Select((points, ring) => points.Count >= 3 ? points.Order().ToArray()
            : points.Union(LayerContours.Handles(rings[ring], step)).Order().ToArray()).ToArray();
        return new(mask, kind, LayerPointLayout.Encode(mask, step, rings, result), [.. selected]);

        LayerControlPoint? Find(PointF position)
        {
            var center = Point.Round(position);
            if (locations.TryGetValue(center, out var exact)) return exact;
            LayerControlPoint? nearest = null;
            var best = 5.01f;
            for (var y = center.Y - 2; y <= center.Y + 2; y++)
                for (var x = center.X - 2; x <= center.X + 2; x++)
                {
                    var dx = x - position.X;
                    var dy = y - position.Y;
                    var distance = dx * dx + dy * dy;
                    if (distance >= best || !locations.TryGetValue(new(x, y), out var point)) continue;
                    nearest = point;
                    best = distance;
                }
            return nearest;
        }
    }
}
