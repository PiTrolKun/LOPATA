using System.Drawing;

namespace AIHub.Services.ImageLayers;

internal static class LayerMagneticPath
{
    private const int MaxSamples = 192;
    private const int MinContrast = 24;

    internal static PointF Snap(LayerSource source, PointF cursor, int radius)
    {
        var best = cursor;
        var bestScore = Strength(cursor);
        for (var y = -radius; y <= radius; y++)
            for (var x = -radius; x <= radius; x++)
            {
                var distance = Math.Sqrt(x * x + y * y);
                if (distance is 0 || distance > radius) continue;
                var point = Clip(source, new(cursor.X + x, cursor.Y + y));
                var score = Strength(point) - 110 * distance / radius;
                if (score <= bestScore) continue;
                best = point;
                bestScore = score;
            }
        return best;

        double Strength(PointF p) => Math.Max(Contrast(source, p, 1, 0), Contrast(source, p, 0, 1));
    }

    internal static PointF[] Trace(LayerSource source, PointF start, PointF end, int radius)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var length = MathF.Sqrt(dx * dx + dy * dy);
        if (length <= 2) return [end];
        var count = Math.Min(MaxSamples, (int)MathF.Ceiling(length));
        var n = 2 * radius + 1;
        var normalX = -dy / length;
        var normalY = dx / length;
        var row = count * n;
        var parents = new int[row];
        var previous = new double[n];
        var next = new double[n];
        Array.Fill(previous, double.PositiveInfinity);
        previous[radius] = 0;
        var maxShift = Math.Clamp((int)MathF.Ceiling(length / count), 2, radius);
        // Follow nearby contrast while penalizing sideways jumps between consecutive samples.
        for (var sample = 1; sample <= count; sample++)
        {
            for (var column = 0; column < n; column++)
            {
                var offset = column - radius;
                var p = Position(sample, offset);
                var cost = 0.11 * Math.Abs(offset) - Contrast(source, p, normalX, normalY) / 74d;
                var best = double.PositiveInfinity;
                var parent = column;
                for (var from = Math.Max(0, column - maxShift); from <= Math.Min(n - 1, column + maxShift); from++)
                {
                    var score = previous[from] + 0.25 * Math.Abs(from - column);
                    if (score >= best) continue;
                    best = score;
                    parent = from;
                }
                next[column] = best + cost;
                parents[(sample - 1) * n + column] = parent;
            }
            (previous, next) = (next, previous);
        }

        var points = new PointF[count];
        // Keep both endpoints fixed so a new segment stays joined to the existing contour.
        var chosen = radius;
        var bent = false;
        for (var sample = count; sample > 0; sample--)
        {
            bent |= chosen != radius;
            points[sample - 1] = Clip(source, Position(sample, chosen - radius));
            chosen = parents[(sample - 1) * n + chosen];
        }
        points[^1] = end;
        return bent ? points : [end];

        PointF Position(int sample, int offset) =>
            new(start.X + dx * sample / count + normalX * offset, start.Y + dy * sample / count + normalY * offset);
    }

    private static PointF Clip(LayerSource source, PointF point) =>
        new(Math.Clamp(point.X, 0, source.Width), Math.Clamp(point.Y, 0, source.Height));

    private static int Contrast(LayerSource source, PointF p, float normalX, float normalY)
    {
        var a = Pixel(p.X - normalX, p.Y - normalY);
        var b = Pixel(p.X + normalX, p.Y + normalY);
        var pixels = source.Pixels;
        var alphaA = pixels[a + 3];
        var alphaB = pixels[b + 3];
        var contrast = Math.Abs(alphaA - alphaB);
        for (var channel = 0; channel < 3; channel++)
            contrast = Math.Max(contrast, Math.Abs(pixels[a + channel] * alphaA - pixels[b + channel] * alphaB) / 255);
        return Math.Max(0, contrast - MinContrast);

        int Pixel(float x, float y) =>
            (Math.Clamp((int)MathF.Floor(y), 0, source.Height - 1) * source.Width
                + Math.Clamp((int)MathF.Floor(x), 0, source.Width - 1)) * 4;
    }
}
