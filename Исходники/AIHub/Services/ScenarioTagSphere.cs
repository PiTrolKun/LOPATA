namespace AIHub.Services;

public readonly record struct SpherePoint(double X, double Y, double Z);
public readonly record struct SphereProjection(double X, double Y, double Depth, double Scale, double Opacity);

/// <summary>Time-based rotation and perspective math, independent of rendering and navigation.</summary>
public static class ScenarioTagSphere
{
    public static IReadOnlyList<SpherePoint> CreatePoints(int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        var points = new SpherePoint[count];
        var goldenAngle = Math.PI * (3 - Math.Sqrt(5));
        for (var i = 0; i < count; i++)
        {
            var y = 1 - 2d * (i + .5) / count;
            var r = Math.Sqrt(1 - y * y);
            points[i] = new(r * Math.Cos(i * goldenAngle), y, r * Math.Sin(i * goldenAngle));
        }
        return Array.AsReadOnly(points);
    }

    public static SphereProjection Project(SpherePoint point, double yaw, double pitch)
    {
        var x = point.X * Math.Cos(yaw) + point.Z * Math.Sin(yaw);
        var z = -point.X * Math.Sin(yaw) + point.Z * Math.Cos(yaw);
        var y = point.Y * Math.Cos(pitch) - z * Math.Sin(pitch);
        z = point.Y * Math.Sin(pitch) + z * Math.Cos(pitch);
        var perspective = 3.5 / (3.5 - z);
        var depth = (z + 1) / 2;
        return new(x * perspective, y * perspective, z, .72 + .43 * depth, .36 + .64 * depth);
    }
}

/// <summary>A completed drag never becomes a click, even if the pointer returns to its origin.</summary>
public sealed class CloudPointerGesture
{
    private double _startX, _startY;
    private string? _tag;
    public bool IsPressed { get; private set; }
    public bool IsDrag { get; private set; }
    public void Begin(double x, double y, string? tag)
    { _startX = x; _startY = y; _tag = tag; IsPressed = true; IsDrag = false; }
    public void Move(double x, double y)
    {
        if (IsPressed && Math.Pow(x - _startX, 2) + Math.Pow(y - _startY, 2) >= 36) IsDrag = true;
    }
    public string? End(double x, double y, string? tag)
    {
        if (!IsPressed) return null;
        Move(x, y);
        var clicked = !IsDrag && tag == _tag ? _tag : null;
        Cancel(); return clicked;
    }
    public void Cancel() { IsPressed = false; IsDrag = false; _tag = null; }
}
