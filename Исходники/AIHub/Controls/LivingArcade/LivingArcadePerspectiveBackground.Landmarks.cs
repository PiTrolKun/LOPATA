using System.Windows;
using System.Windows.Media;
using Point = System.Windows.Point;

namespace AIHub.Controls.LivingArcade;

public sealed partial class LivingArcadePerspectiveBackground
{
    private DrawingGroup BuildLandmark(int variant)
    {
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            dc.PushOpacity(.35);
            dc.DrawEllipse(B(_palette.Dark), null, new Point(6, 5), 67, 12);
            dc.Pop();
            // Three silhouettes each have a second, taller/narrower or mirrored prefab.
            if (variant >= 3) dc.PushTransform(new ScaleTransform(variant % 2 == 0 ? -.88 : .88, 1.12));
            switch (SceneKind)
            {
                case "harbor": DrawHarbor(dc, variant); break;
                case "forest": DrawForest(dc, variant); break;
                case "desert": DrawDesert(dc, variant); break;
                case "ice": DrawIce(dc, variant); break;
                case "city": DrawCity(dc, variant); break;
                default: DrawAlien(dc, variant); break;
            }
            if (variant >= 3) dc.Pop();
        }
        drawing.Freeze(); return drawing;
    }

    private void DrawHarbor(DrawingContext dc, int variant)
    {
        if (variant % 3 < 2)
        {
            var stacked = variant % 3 == 1;
            Box(dc, -57, -55, 105, 55, 20);
            for (var x = -49; x < 46; x += 13) Line(dc, _palette.Dark, x, -50, x, -5, 2);
            Rect(dc, _palette.Accent, -42, -45, 25, 10);
            Line(dc, _palette.Light, -54, -4, 48, -4, 2);
            if (stacked)
            {
                Box(dc, -44, -109, 82, 49, 17);
                for (var x = -36; x < 37; x += 12) Line(dc, _palette.Dark, x, -104, x, -65, 2);
                Rect(dc, _palette.Light, 11, -98, 14, 7);
            }
            else
            {
                Rect(dc, _palette.Dark, -48, -2, 13, 9);
                Rect(dc, _palette.Dark, 30, -2, 13, 9);
            }
        }
        else
        {
            Box(dc, -35, -20, 65, 20, 12);
            Box(dc, -17, -154, 25, 134, 10);
            Line(dc, _palette.Light, -10, -150, 66, -126, 7);
            Line(dc, _palette.Dark, 1, -150, -65, -131, 5);
            Line(dc, _palette.Mid, -7, -145, 61, -129, 3);
            Line(dc, _palette.Light, 62, -127, 62, -72, 2);
            Line(dc, _palette.Dark, 57, -72, 67, -72, 4);
            for (var y = -131; y < -25; y += 25)
            {
                Line(dc, _palette.Dark, -15, y, 7, y + 19, 2);
                Line(dc, _palette.Dark, 7, y, -15, y + 19, 2);
            }
            Rect(dc, _palette.Accent, -29, -17, 16, 9);
        }
    }

    private void DrawForest(DrawingContext dc, int variant)
    {
        if (variant % 3 == 0)
        {
            Rect(dc, _palette.Dark, -10, -115, 24, 117);
            Rect(dc, _palette.Mid, -5, -112, 7, 108);
            for (var layer = 0; layer < 4; layer++)
            {
                var y = -179 + layer * 31; var w = 25 + layer * 13;
                Shape(dc, _palette.Mid, [new(0, y), new(w, y + 66), new(-w, y + 66)]);
                Shape(dc, _palette.Dark, [new(0, y), new(w, y + 66), new(7, y + 60)]);
                Line(dc, _palette.Light, -w + 9, y + 60, -7, y + 42, 2);
            }
        }
        else if (variant % 3 == 1)
        {
            Rock(dc, 64, 64);
            Shape(dc, _palette.Mid, [new(-52, -15), new(-28, -29), new(3, -21), new(21, -6), new(-35, 1)]);
            Line(dc, _palette.Accent, -30, -31, -40, -50, 3);
            Line(dc, _palette.Accent, -40, -50, -49, -45, 3);
        }
        else
        {
            Box(dc, -63, -89, 27, 89, 13);
            Box(dc, 29, -89, 27, 89, 13);
            Box(dc, -63, -116, 119, 27, 13);
            for (var y = -77; y < -8; y += 24)
            {
                Line(dc, _palette.Dark, -62, y, -37, y, 2);
                Line(dc, _palette.Dark, 30, y + 7, 55, y + 7, 2);
            }
            Line(dc, _palette.Accent, -57, -109, -50, -73, 4);
            Line(dc, _palette.Accent, -50, -73, -59, -43, 3);
        }
    }

    private void DrawDesert(DrawingContext dc, int variant)
    {
        if (variant % 3 == 0)
        {
            Line(dc, _palette.Dark, 0, 0, 0, -140, 21);
            Line(dc, _palette.Mid, -3, -1, -3, -139, 12);
            Line(dc, _palette.Mid, -2, -69, -39, -69, 15);
            Line(dc, _palette.Mid, -39, -69, -39, -106, 15);
            Line(dc, _palette.Mid, 0, -92, 33, -92, 13);
            Line(dc, _palette.Mid, 33, -92, 33, -122, 13);
            Line(dc, _palette.Light, -6, -133, -6, -8, 2);
            Line(dc, _palette.Light, -43, -102, -43, -73, 2);
        }
        else if (variant % 3 == 1)
        {
            Shape(dc, _palette.Mid, [new(-76, 0), new(-53, -49), new(-44, -137), new(18, -142), new(45, -103), new(61, -36), new(76, 0)]);
            Shape(dc, _palette.Dark, [new(18, -142), new(45, -103), new(61, -36), new(76, 0), new(14, -3), new(-2, -90)]);
            Shape(dc, _palette.Light, [new(-44, -137), new(18, -142), new(31, -124), new(-39, -120)]);
            for (var y = -97; y < -9; y += 26) Line(dc, _palette.Dark, -42, y, 2, y - 3, 2);
        }
        else
        {
            Box(dc, -48, -76, 75, 76, 23);
            Box(dc, -59, -91, 98, 15, 17);
            Rect(dc, _palette.Dark, -25, -54, 26, 54);
            Line(dc, _palette.Light, -43, -69, -43, -8, 2);
            Line(dc, _palette.Dark, -45, -58, -24, -59, 2);
            Rock(dc, 35, 24);
        }
    }

    private void DrawIce(DrawingContext dc, int variant)
    {
        if (variant % 3 == 0)
        {
            Crystal(dc, -29, 1, 29, 114);
            Crystal(dc, 12, 2, 43, 173);
            Crystal(dc, 52, 4, 20, 75);
        }
        else if (variant % 3 == 1)
        {
            Box(dc, -55, -69, 95, 69, 24);
            Shape(dc, _palette.Light, [new(-64, -71), new(-3, -105), new(53, -78), new(40, -69), new(-55, -69)]);
            Rect(dc, _palette.Dark, -38, -47, 26, 19);
            Rect(dc, _palette.Accent, -36, -45, 22, 5);
            Rect(dc, _palette.Dark, 8, -49, 22, 49);
            Rect(dc, _palette.Light, -55, -7, 95, 7);
        }
        else
        {
            Box(dc, -22, -29, 45, 29, 15);
            Line(dc, _palette.Light, 0, -30, 0, -164, 5);
            Line(dc, _palette.Mid, -35, -102, 35, -102, 5);
            Line(dc, _palette.Dark, -30, -98, -30, -134, 3);
            Line(dc, _palette.Dark, 30, -98, 30, -127, 3);
            Shape(dc, _palette.Mid, [new(0, -151), new(45, -145), new(19, -120)]);
            Line(dc, _palette.Accent, 0, -165, 0, -177, 4);
        }
    }

    private void DrawCity(DrawingContext dc, int variant)
    {
        if (variant % 3 < 2)
        {
            var h = variant % 3 == 0 ? 175 : 95;
            Box(dc, -49, -h, 76, h, 28);
            Box(dc, -39, -h - 14, 56, 14, 18);
            for (var y = -h + 15; y < -14; y += 27)
                for (var x = -37; x < 22; x += 19)
                {
                    Rect(dc, _palette.Dark, x, y, 11, 14);
                    if ((x + y) % 3 != 0) Rect(dc, _palette.Accent, x + 2, y + 2, 7, 5);
                }
            Rect(dc, _palette.Dark, -19, -26, 20, 26);
            Line(dc, _palette.Light, -46, -h + 2, -46, -2, 2);
        }
        else
        {
            Box(dc, -14, -11, 26, 11, 8);
            Line(dc, _palette.Dark, 0, -10, 0, -164, 7);
            Line(dc, _palette.Light, -2, -12, -2, -163, 2);
            Line(dc, _palette.Mid, -2, -164, 48, -164, 6);
            Box(dc, 28, -173, 35, 14, 10);
            Rect(dc, _palette.Accent, 33, -159, 28, 4);
            Box(dc, -60, -37, 28, 37, 10);
            Line(dc, _palette.Dark, -56, -31, -35, -31, 2);
        }
    }

    private void DrawAlien(DrawingContext dc, int variant)
    {
        if (variant % 3 == 0)
        {
            Crystal(dc, -24, 3, 39, 170);
            Crystal(dc, 28, 6, 32, 114);
            Line(dc, _palette.Accent, -35, -76, -29, -124, 3);
        }
        else if (variant % 3 == 1)
        {
            Shape(dc, _palette.Dark, [new(-24, 0), new(-7, -88), new(-13, -119), new(12, -119), new(20, -80), new(33, 0)]);
            Shape(dc, _palette.Mid, [new(-7, -2), new(-2, -109), new(9, -110), new(14, -4)]);
            Shape(dc, _palette.Mid, [new(-73, -113), new(-43, -145), new(-9, -164), new(28, -156), new(72, -115), new(37, -104), new(-40, -103)]);
            Shape(dc, _palette.Light, [new(-73, -113), new(-43, -145), new(-9, -164), new(-20, -125)]);
            Line(dc, _palette.Accent, -48, -112, 48, -112, 3);
        }
        else
        {
            Box(dc, -56, -107, 23, 107, 15);
            Box(dc, 27, -107, 23, 107, 15);
            Box(dc, -56, -136, 106, 29, 15);
            for (var y = -98; y < -10; y += 22)
            {
                Rect(dc, _palette.Accent, -49, y, 5, 9);
                Rect(dc, _palette.Accent, 34, y, 5, 9);
            }
            Shape(dc, _palette.Light, [new(-17, -127), new(-7, -116), new(4, -127), new(-7, -131)]);
        }
    }

    private void Box(DrawingContext dc, double x, double y, double w, double h, double depth)
    {
        Rect(dc, _palette.Mid, x, y, w, h);
        Shape(dc, _palette.Dark, [new(x + w, y), new(x + w + depth, y - depth * .5), new(x + w + depth, y + h - depth * .5), new(x + w, y + h)]);
        Shape(dc, _palette.Light, [new(x, y), new(x + depth, y - depth * .5), new(x + w + depth, y - depth * .5), new(x + w, y)]);
    }

    private void Crystal(DrawingContext dc, double x, double y, double w, double h)
    {
        Shape(dc, _palette.Mid, [new(x - w * .5, y - h * .72), new(x, y - h), new(x + w * .5, y - h * .77), new(x + w * .44, y), new(x - w * .45, y)]);
        Shape(dc, _palette.Dark, [new(x, y - h), new(x + w * .5, y - h * .77), new(x + w * .44, y), new(x + 2, y - h * .14)]);
        Line(dc, _palette.Light, x, y - h + 9, x - w * .29, y - h * .68, 2);
    }

    private void Rock(DrawingContext dc, double w, double h)
    {
        Shape(dc, _palette.Mid, [new(-w, 0), new(-w * .8, -h * .65), new(-w * .28, -h), new(w * .45, -h * .84), new(w, -h * .27), new(w * .85, 3)]);
        Shape(dc, _palette.Dark, [new(-w * .28, -h), new(w * .45, -h * .84), new(w, -h * .27), new(w * .85, 3), new(w * .16, -h * .14)]);
        Line(dc, _palette.Light, -w * .74, -h * .62, -w * .28, -h * .89, 2);
    }
}
