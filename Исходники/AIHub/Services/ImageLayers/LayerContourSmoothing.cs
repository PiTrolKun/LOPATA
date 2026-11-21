using System.Drawing;

namespace AIHub.Services.ImageLayers;

internal static class LayerContourSmoothing
{
    private static readonly int[] Weights = [70, 56, 28, 8, 1];
    private const int CornerRun = 4;
    private const float MaximumOffset = .5f;

    // Keep a one-to-one correspondence with the raster vertices used by saved control points.
    internal static PointF[] Smooth(Point[] ring)
    {
        var length = ring.Length;
        var smooth = new PointF[length];
        var radius = Math.Min(Weights.Length - 1, (length - 1) / 2);
        var corners = new bool[length];
        if (length > CornerRun * 2)
            for (var i = 0; i < length; i++)
            {
                var at = ring[i];
                var before = At(i - 1);
                var after = At(i + 1);
                if ((before.X - at.X) * (after.X - at.X) + (before.Y - at.Y) * (after.Y - at.Y) != 0) continue;
                corners[i] = KeepsDirection(before, At(i - CornerRun), at)
                    && KeepsDirection(after, At(i + CornerRun), at);
            }

        for (var i = 0; i < length; i++)
        {
            var at = ring[i];
            if (corners[i])
            {
                smooth[i] = at;
                continue;
            }

            var dx = 0;
            var dy = 0;
            var sum = Weights[0];
            for (var offset = 1; offset <= radius; offset++)
            {
                var left = Index(i - offset);
                var right = Index(i + offset);
                var weight = Weights[offset];
                dx += weight * (ring[left].X + ring[right].X - 2 * at.X);
                dy += weight * (ring[left].Y + ring[right].Y - 2 * at.Y);
                sum += 2 * weight;
                if (corners[left] || corners[right]) break;
            }

            // Limit the movement at the pixel boundary so narrow features are not smoothed away.
            var magnitude = MathF.Sqrt(dx * dx + dy * dy);
            var divisor = Math.Max(sum, magnitude / MaximumOffset);
            smooth[i] = new(at.X + dx / divisor, at.Y + dy / divisor);
        }
        return smooth;

        int Index(int index) => index < 0 ? index + length : index >= length ? index - length : index;
        Point At(int index) => ring[Index(index)];
    }

    private static bool KeepsDirection(Point next, Point end, Point corner) =>
        end.X - corner.X == (next.X - corner.X) * CornerRun && end.Y - corner.Y == (next.Y - corner.Y) * CornerRun;
}
