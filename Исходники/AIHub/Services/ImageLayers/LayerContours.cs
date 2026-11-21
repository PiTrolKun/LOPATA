using System.Drawing;
using SkiaSharp;

namespace AIHub.Services.ImageLayers;

internal static class LayerContours
{
    internal static List<Point[]> Trace(LayerMask mask, int width)
    {
        var height = mask.Length / width;
        var stride = width + 1;
        var edges = new Dictionary<int, byte>();
        var bounds = mask.Bounds(width);
        for (var y = bounds.Top; y < bounds.Bottom; y++)
            for (var x = bounds.Left; x < bounds.Right; x++)
            {
                var i = y * width + x;
                if (!mask[i]) continue;
                var vertex = y * stride + x;
                if (y == 0 || !mask[i - width]) Add(vertex, 0);
                if (x == width - 1 || !mask[i + 1]) Add(vertex + 1, 1);
                if (y == height - 1 || !mask[i + width]) Add(vertex + stride + 1, 2);
                if (x == 0 || !mask[i - 1]) Add(vertex + stride, 3);
            }

        var rings = new List<Point[]>();
        int[] offsets = [1, stride, -1, -stride];
        foreach (var start in edges.Keys.ToArray())
            while (edges.TryGetValue(start, out var remaining))
            {
                var direction = System.Numerics.BitOperations.TrailingZeroCount((uint)remaining);
                var node = start;
                var ring = new List<Point>();
                do
                {
                    ring.Add(new(node % stride, node / stride));
                    var rest = (byte)(edges[node] & ~(1 << direction));
                    if (rest == 0) edges.Remove(node); else edges[node] = rest;
                    node += offsets[direction];
                    if (node == start) break;
                    var outgoing = edges[node];
                    // Turn right first so diagonal neighbours remain individually closed.
                    for (var turn = 1; turn >= -2; turn--)
                    {
                        var next = (direction + turn + 4) % 4;
                        if ((outgoing & (1 << next)) == 0) continue;
                        direction = next;
                        break;
                    }
                } while (true);
                rings.Add(ring.ToArray());
            }
        return rings;

        void Add(int start, int direction) => edges[start] = (byte)(edges.GetValueOrDefault(start) | (1 << direction));
    }

    internal static int[] Handles(Point[] ring, int step)
    {
        var count = Math.Clamp((int)Math.Ceiling(ring.Length / (double)step), Math.Min(3, ring.Length), ring.Length);
        return Enumerable.Range(0, count).Select(i => (int)((long)i * ring.Length / count)).ToArray();
    }

    internal static LayerMask Warp(LayerMask mask, LayerSource source, Point[] ring, int[] handles,
        IReadOnlyDictionary<int, PointF> positions)
    {
        var altered = new PointF[ring.Length];
        for (var n = 0; n < handles.Length; n++)
        {
            var from = handles[n];
            var end = n + 1 == handles.Length ? handles[0] + ring.Length : handles[n + 1];
            var to = end % ring.Length;
            var a = positions.TryGetValue(from, out var start) ? Subtract(start, ring[from]) : PointF.Empty;
            var b = positions.TryGetValue(to, out var finish) ? Subtract(finish, ring[to]) : PointF.Empty;
            for (var i = from; i < end; i++)
            {
                var mix = (i - from) / (float)(end - from);
                var vertex = i % ring.Length;
                altered[vertex] = new(Math.Clamp(ring[vertex].X + (1 - mix) * a.X + mix * b.X, 0, source.Width),
                    Math.Clamp(ring[vertex].Y + (1 - mix) * a.Y + mix * b.Y, 0, source.Height));
            }
        }
        return ReplaceArc(mask, source, ring, altered);
    }

    internal static LayerMask Reconnect(LayerMask mask, LayerSource source, Point[] ring, int[] handles, int[] retained)
    {
        var altered = Array.ConvertAll(ring, point => (PointF)point);
        for (var index = 0; index < retained.Length; index++)
        {
            var from = retained[index];
            var to = retained[(index + 1) % retained.Length];
            var ordinal = Array.BinarySearch(handles, from);
            if (handles[(ordinal + 1) % handles.Length] == to) continue;
            var end = to > from ? to : to + ring.Length;
            var delta = Subtract(ring[to], ring[from]);
            for (var vertex = from + 1; vertex < end; vertex++)
            {
                var mix = (vertex - from) / (float)(end - from);
                altered[vertex % ring.Length] = new(ring[from].X + delta.X * mix, ring[from].Y + delta.Y * mix);
            }
        }
        return ReplaceArc(mask, source, ring, altered);
    }

    internal static bool IsHole(Point[] ring)
    {
        long twiceArea = 0;
        for (var i = 0; i < ring.Length; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Length];
            twiceArea += (long)a.X * b.Y - (long)b.X * a.Y;
        }
        return twiceArea < 0;
    }

    internal static LayerMask RingArea(Point[] ring, LayerSource source) => Fill(Array.ConvertAll(ring, point => (PointF)point), source);

    internal static LayerMask WithoutContoursInside(LayerMask mask, LayerSource source, Rectangle area, IEnumerable<Point[]> rings)
    {
        using var path = new SKPath { FillType = SKPathFillType.EvenOdd };
        foreach (var ring in rings)
        {
            // Vertices lie on pixel borders, where the right and bottom edges are valid.
            if (ring.All(point => point.X >= area.Left && point.X <= area.Right && point.Y >= area.Top && point.Y <= area.Bottom))
                path.AddPoly(Array.ConvertAll(ring, point => new SKPoint(point.X - area.X, point.Y - area.Y)), close: true);
        }
        // XOR cancels the interior of each removed ring, including nested holes and islands.
        return path.IsEmpty ? mask : mask.Changes(Fill(path, area, source));
    }

    private static LayerMask ReplaceArc(LayerMask mask, LayerSource source, Point[] ring, PointF[] altered)
    {
        var firstAffected = ring.Length;
        var lastAffected = -1;
        for (var index = 0; index < ring.Length; index++)
        {
            if ((PointF)ring[index] == altered[index]) continue;
            firstAffected = Math.Min(index, firstAffected);
            lastAffected = index;
        }
        if (lastAffected < 0) return mask;
        if (lastAffected - firstAffected >= ring.Length - 2)
            return mask.Changes(RingArea(ring, source)).Changes(Fill(altered, source));
        // XOR just the strip between the old and new path. Keeping the other pixels avoids
        // rasterizing unrelated edges again when a handle on a detailed contour is moved.
        var first = firstAffected - 1;
        var last = lastAffected + 1;
        var strip = new List<PointF>();
        for (var i = first; i <= last; i++) strip.Add(ring[(i + ring.Length) % ring.Length]);
        for (var i = last; i >= first; i--) strip.Add(altered[(i + ring.Length) % ring.Length]);
        return mask.Changes(Fill(strip, source));
    }

    internal static Dictionary<int, PointF> Pencil(PointF[] ring, int[] handles, IReadOnlyList<PointF> stroke, float radius)
    {
        var result = new Dictionary<int, PointF>();
        foreach (var index in handles)
        {
            var near = Nearest(ring[index], stroke);
            var delta = Subtract(near, ring[index]);
            if (delta.X * delta.X + delta.Y * delta.Y <= radius * radius) result[index] = near;
        }
        return result;
    }

    internal static PointF Nearest(PointF point, IReadOnlyList<PointF> line)
    {
        var best = line[0];
        var distance = float.PositiveInfinity;
        for (var i = 1; i < line.Count; i++)
        {
            var a = line[i - 1]; var b = line[i];
            var d = Subtract(b, a);
            var length = d.X * d.X + d.Y * d.Y;
            var t = length == 0 ? 0 : Math.Clamp(((point.X - a.X) * d.X + (point.Y - a.Y) * d.Y) / length, 0, 1);
            var p = new PointF(a.X + t * d.X, a.Y + t * d.Y);
            var diff = Subtract(point, p);
            var squared = diff.X * diff.X + diff.Y * diff.Y;
            if (squared < distance) { best = p; distance = squared; }
        }
        return best;
    }

    private static PointF Subtract(PointF a, PointF b) => new(a.X - b.X, a.Y - b.Y);

    internal static LayerMask Fill(IReadOnlyList<PointF> path, LayerSource source)
    {
        var left = (int)Math.Floor(path.Min(point => point.X));
        var top = (int)Math.Floor(path.Min(point => point.Y));
        var width = (int)Math.Ceiling(path.Max(point => point.X)) - left;
        var height = (int)Math.Ceiling(path.Max(point => point.Y)) - top;
        if (width == 0 || height == 0) return new(source.Width * source.Height);
        using var polygon = new SKPath { FillType = SKPathFillType.EvenOdd };
        polygon.AddPoly(path.Select(point => new SKPoint(point.X - left, point.Y - top)).ToArray(), close: true);
        return Fill(polygon, new(left, top, width, height), source);
    }

    private static LayerMask Fill(SKPath path, Rectangle area, LayerSource source)
    {
        using var bitmap = new SKBitmap(area.Width, area.Height, SKColorType.Alpha8, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = false };
        canvas.Clear(SKColors.Transparent);
        canvas.DrawPath(path, paint);
        canvas.Flush();
        var pixels = bitmap.Bytes;
        return LayerMask.Build(source.Width * source.Height, mask =>
        {
            for (var y = 0; y < area.Height; y++)
                for (var x = 0; x < area.Width; x++)
                    if (pixels[y * bitmap.RowBytes + x] != 0) mask[(area.Top + y) * source.Width + area.Left + x] = true;
        });
    }
}
