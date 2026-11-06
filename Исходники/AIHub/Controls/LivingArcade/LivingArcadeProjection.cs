using System.Windows;
using Point = System.Windows.Point;

namespace AIHub.Controls.LivingArcade;

/// <summary>One ground-plane projection for scenery, actors, projectiles and aiming. Simulation stays in world coordinates.</summary>
public readonly struct LivingArcadeProjection(bool perspective, double left, double width, double height)
{
    public const double HorizonFraction = .12;
    public const double BottomFraction = 1.10;
    public const double PerspectiveDepth = 2.5;
    private const double DepthSlope = PerspectiveDepth - 1;

    public Point Project(double x, double y)
    {
        x = double.IsFinite(x) ? x : .5;
        y = double.IsFinite(y) ? y : 0;
        var safeLeft = double.IsFinite(left) ? left : 0;
        var safeWidth = double.IsFinite(width) && width > 0 ? width : 0;
        var safeHeight = double.IsFinite(height) && height > 0 ? height : 0;
        if (!perspective) return new(safeLeft + x * safeWidth, y * safeHeight);
        y = Math.Clamp(y, -.2, 1.2);
        var depth = ScaleAt(y);
        return new(safeLeft + safeWidth * .5 + (x - .5) * safeWidth * depth,
            safeHeight * (HorizonFraction + (BottomFraction - HorizonFraction) * y * depth));
    }

    public double ScaleAt(double y) => !perspective ? 1
        : 1 / (PerspectiveDepth - DepthSlope * Math.Clamp(double.IsFinite(y) ? y : 0, -.2, 1.2));

    /// <summary>Project the local shot direction, so a sideways hero still points along its actual shot path.</summary>
    public double AimAngle(double x, double y, double angle)
    {
        angle = double.IsFinite(angle) ? angle : 0;
        if (!perspective) return angle;
        x = double.IsFinite(x) ? x : .5;
        var depth = ScaleAt(y);
        var dy = -Math.Cos(angle);
        var dx = Math.Sin(angle);
        var screenX = width * (dx * depth + (x - .5) * DepthSlope * depth * depth * dy);
        var screenY = height * (BottomFraction - HorizonFraction) * PerspectiveDepth * depth * depth * dy;
        return double.IsFinite(screenX) && double.IsFinite(screenY) ? Math.Atan2(screenX, -screenY) : 0;
    }
}
