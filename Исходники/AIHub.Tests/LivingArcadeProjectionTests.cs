using AIHub.Controls.LivingArcade;

namespace AIHub.Tests;

[TestClass]
public sealed class LivingArcadeProjectionTests
{
    [TestMethod]
    public void TopDownKeepsTheExistingAffineCoordinatesAndAim()
    {
        var projection = new LivingArcadeProjection(false, 137, 640, 600);
        foreach (var x in new[] { -.1, 0, .2, .5, 1, 1.1 })
        foreach (var y in new[] { -.2, 0, .3, .86, 1, 1.2 })
        {
            var point = projection.Project(x, y);
            Assert.AreEqual(137 + x * 640, point.X, 1e-10);
            Assert.AreEqual(y * 600, point.Y, 1e-10);
            Assert.AreEqual(1d, projection.ScaleAt(y), 1e-10);
            foreach (var angle in new[] { -.17, 0, .17 })
                Assert.AreEqual(angle, projection.AimAngle(x, y, angle), 1e-10);
        }
    }

    [TestMethod]
    public void PerspectiveHasOneHorizonAndSymmetricNonCrossingDepthLines()
    {
        var projection = new LivingArcadeProjection(true, 80, 640, 600);
        var previousY = double.NegativeInfinity;
        var previousScale = 0d;
        for (var step = 0; step <= 140; step++)
        {
            var depth = -.2 + step / 100d;
            var left = projection.Project(.1, depth);
            var center = projection.Project(.5, depth);
            var right = projection.Project(.9, depth);
            Assert.IsTrue(double.IsFinite(left.X) && double.IsFinite(left.Y));
            Assert.IsTrue(left.X < center.X && center.X < right.X, "Depth must not reverse lateral order.");
            Assert.AreEqual(400d, center.X, 1e-9);
            Assert.AreEqual(center.X - left.X, right.X - center.X, 1e-9);
            Assert.IsTrue(center.Y > previousY, "An approaching object must always advance on screen.");
            Assert.IsTrue(projection.ScaleAt(depth) > previousScale, "An approaching object must grow continuously.");
            previousY = center.Y; previousScale = projection.ScaleAt(depth);
        }
        Assert.AreEqual(600 * LivingArcadeProjection.HorizonFraction, projection.Project(.5, 0).Y, 1e-9);
        Assert.IsTrue(projection.ScaleAt(.9) > projection.ScaleAt(.1) * 1.8,
            "Distant and foreground enemies must have noticeably different sizes.");
        foreach (var depth in new[] { -1_000_000d, -.21, 1.21, 1_000_000 })
        {
            var point = projection.Project(.3, depth);
            Assert.IsTrue(double.IsFinite(point.X) && double.IsFinite(point.Y) && double.IsFinite(projection.ScaleAt(depth)),
                "Queued off-screen entities must never introduce a singular drawing transform.");
        }
    }

    [TestMethod]
    public void AimFollowsTheSameProjectedWorldRayAsTheProjectile()
    {
        var projection = new LivingArcadeProjection(true, 80, 640, 600);
        foreach (var x in new[] { .15, .5, .85 })
        foreach (var y in new[] { .15, .5, .86 })
        foreach (var angle in new[] { -.17, 0, .17 })
        {
            var origin = projection.Project(x, y);
            var next = projection.Project(x + Math.Sin(angle) * 1e-5, y - Math.Cos(angle) * 1e-5);
            var tangent = next - origin;
            tangent.Normalize();
            var projectedAim = projection.AimAngle(x, y, angle);
            var gunDirection = new System.Windows.Vector(Math.Sin(projectedAim), -Math.Cos(projectedAim));
            Assert.IsTrue(System.Windows.Vector.Multiply(tangent, gunDirection) > .999999,
                "The rendered weapon must point along the same ray as its projectiles, also away from the road center.");
        }
    }

    [TestMethod]
    public void InvalidQueuedGeometryCannotPropagateNaNToWpf()
    {
        foreach (var perspective in new[] { false, true })
        foreach (var value in new[] { double.NaN, double.NegativeInfinity, double.PositiveInfinity })
        {
            var projection = new LivingArcadeProjection(perspective, value, value, value);
            var point = projection.Project(value, value);
            Assert.IsTrue(double.IsFinite(point.X) && double.IsFinite(point.Y));
            Assert.IsTrue(double.IsFinite(projection.ScaleAt(value)));
            Assert.IsTrue(double.IsFinite(projection.AimAngle(value, value, value)));
        }
    }
}
