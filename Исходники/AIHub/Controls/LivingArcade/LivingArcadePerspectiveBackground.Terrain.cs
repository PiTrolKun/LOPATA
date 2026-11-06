using System.Windows;
using System.Windows.Media;
using Point = System.Windows.Point;

namespace AIHub.Controls.LivingArcade;

public sealed partial class LivingArcadePerspectiveBackground
{
    private void DrawTerrain(DrawingContext dc, LivingArcadeProjection projection, double width, double height, double progress)
    {
        // Keep partially visible world rows: their markings can extend beyond the row's own anchor.
        // Clipping the ground preserves their gradual arrival at the horizon across recycling boundaries.
        var horizon = height * LivingArcadeProjection.HorizonFraction;
        dc.PushClip(new RectangleGeometry(new Rect(0, horizon, width, height - horizon)));
        var floor = new StreamGeometry();
        using (var path = floor.Open()) AddQuad(path, projection, 0, 0, 1, 1.2);
        dc.DrawGeometry(B(_palette.Floor), null, floor);

        var shoulder = new StreamGeometry();
        using (var path = shoulder.Open())
        {
            AddQuad(path, projection, -.10, 0, 0, 1.2);
            AddQuad(path, projection, 1, 0, 1.10, 1.2);
        }
        dc.DrawGeometry(B(_palette.Seam), null, shoulder);

        // A fixed number of projected world rows scrolls continuously under the camera.
        // Geometry is batched into two drawings, rather than individual retained road objects.
        var seams = new StreamGeometry();
        var markings = new StreamGeometry();
        var distance = progress * .095;
        var shift = PositiveMod(distance, .07);
        var originRow = Math.Floor(distance / .07);
        if (!double.IsFinite(originRow)) originRow = 0;
        using (var grid = seams.Open())
        using (var marks = markings.Open())
        {
            AddSegment(grid, projection, 0, 0, 0, 1.2);
            AddSegment(grid, projection, 1, 0, 1, 1.2);
            for (var i = 0; i < 21; i++)
            {
                var y = i * .07 + shift - .14;
                // The pattern belongs to the world row, not its recycled screen slot.
                // When scrolling wraps, the same row moves to i + 1 without changing its design.
                var row = i - originRow;
                switch (SceneKind)
                {
                    case "harbor": DrawDockRow(grid, marks, projection, row, y); break;
                    case "forest": DrawForestRow(grid, marks, projection, row, y); break;
                    case "desert": DrawDesertRow(grid, projection, row, y); break;
                    case "ice": DrawIceRow(grid, marks, projection, row, y); break;
                    case "city": DrawCityRow(grid, marks, projection, row, y); break;
                    default: DrawAlienRow(grid, marks, projection, row, y); break;
                }
            }
            if (SceneKind is "city" or "harbor")
                for (var lane = 1; lane < 4; lane++) AddSegment(grid, projection, lane * .25, 0, lane * .25, 1.2);
            if (SceneKind == "desert")
            {
                AddSegment(grid, projection, .27, 0, .27, 1.2);
                AddSegment(grid, projection, .73, 0, .73, 1.2);
            }
        }
        dc.DrawGeometry(null, P(_palette.Seam, 1), seams);
        dc.DrawGeometry(B(_palette.Mark), null, markings);
        dc.Pop();
    }

    private static void DrawDockRow(StreamGeometryContext grid, StreamGeometryContext marks,
        LivingArcadeProjection projection, double row, double y)
    {
        AddSegment(grid, projection, 0, y, 1, y);
        if (row % 2 == 0)
        {
            AddQuad(marks, projection, -.078, y, -.045, y + .025);
            AddQuad(marks, projection, 1.045, y, 1.078, y + .025);
        }
    }

    private static void DrawForestRow(StreamGeometryContext grid, StreamGeometryContext marks,
        LivingArcadeProjection projection, double row, double y)
    {
        // Broken paving and small moss patches stay flat, unlike the roadside tree billboards.
        var offset = PositiveMod(row, 2) * .10;
        for (var tile = 0; tile < 4; tile++)
        {
            var x = tile * .25 + offset;
            AddSegment(grid, projection, x, y, Math.Min(1, x + .19), y + .008);
            AddSegment(grid, projection, x, y, x - .018, y + .035);
        }
        if (row % 3 == 0)
        {
            AddQuad(marks, projection, -.065, y, -.03, y + .012);
            AddQuad(marks, projection, 1.025, y + .02, 1.065, y + .032);
        }
    }

    private static void DrawDesertRow(StreamGeometryContext grid, LivingArcadeProjection projection, double row, double y)
    {
        if (row % 2 != 0) return;
        for (var segment = 0; segment < 8; segment++)
        {
            var x = -.5 + segment * .25;
            var ripple = Math.Sin((segment + row * .31) * 1.6) * .013;
            var nextRipple = Math.Sin((segment + 1 + row * .31) * 1.6) * .013;
            AddSegment(grid, projection, x, y + ripple, x + .25, y + nextRipple);
        }
    }

    private static void DrawIceRow(StreamGeometryContext grid, StreamGeometryContext marks,
        LivingArcadeProjection projection, double row, double y)
    {
        var x = PositiveMod(PositiveMod(row, 91) * 37, 91) / 100d;
        AddSegment(grid, projection, x - .12, y, x + .10, y + .025);
        AddSegment(grid, projection, x + .10, y + .025, x + .045, y + .050);
        if (row % 3 != 0) return;
        AddQuad(marks, projection, -.09, y, -.055, y + .018);
        AddQuad(marks, projection, 1.055, y, 1.09, y + .018);
    }

    private static void DrawCityRow(StreamGeometryContext grid, StreamGeometryContext marks,
        LivingArcadeProjection projection, double row, double y)
    {
        if (row % 2 == 0)
        {
            AddQuad(marks, projection, .493, y, .507, y + .035);
            AddSegment(grid, projection, -.1, y, 0, y);
            AddSegment(grid, projection, 1, y, 1.1, y);
        }
        if (row % 5 == 0) AddSegment(grid, projection, 0, y, 1, y);
    }

    private static void DrawAlienRow(StreamGeometryContext grid, StreamGeometryContext marks,
        LivingArcadeProjection projection, double row, double y)
    {
        for (var tile = 0; tile < 3; tile++)
        {
            var x = tile / 3d;
            AddSegment(grid, projection, x, y, x + .16, y + .012);
            AddSegment(grid, projection, x + .16, y + .012, x + .30, y);
            AddSegment(grid, projection, x + .16, y + .012, x + .16, y + .05);
        }
        if (row % 3 == 0)
        {
            AddQuad(marks, projection, -.075, y, -.04, y + .012);
            AddQuad(marks, projection, 1.04, y, 1.075, y + .012);
        }
    }

    private static void AddQuad(StreamGeometryContext path, LivingArcadeProjection projection,
        double left, double top, double right, double bottom)
    {
        path.BeginFigure(projection.Project(left, top), true, true);
        path.LineTo(projection.Project(right, top), true, false);
        path.LineTo(projection.Project(right, bottom), true, false);
        path.LineTo(projection.Project(left, bottom), true, false);
    }

    private static void AddSegment(StreamGeometryContext path, LivingArcadeProjection projection,
        double x, double y, double toX, double toY)
    {
        path.BeginFigure(projection.Project(x, y), false, false);
        path.LineTo(projection.Project(toX, toY), true, false);
    }
}
