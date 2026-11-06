using System.Windows.Media;
using Point = System.Windows.Point;

namespace AIHub.Controls.LivingArcade;

public sealed partial class LivingArcadeBackground
{
    private void DrawSide(DrawingContext dc, Random random, bool right)
    {
        // Both strips face the corridor, but their seeds and landmarks differ.
        dc.PushTransform(right ? new MatrixTransform(-1, 0, 0, 1, Side, 0) : Transform.Identity);
        Rect(dc, _palette.Ground, 0, 0, Side, Tile);
        for (var i = 0; i < 95; i++)
        {
            var x = random.NextDouble() * Side; var y = random.NextDouble() * Tile;
            Rect(dc, i % 3 == 0 ? _palette.Mid : _palette.Dark, x, y, random.Next(2, 8), random.Next(1, 4));
        }
        switch (SceneKind)
        {
            case "harbor": DockSide(dc, random); break;
            case "forest": ForestSide(dc, random); break;
            case "desert": DesertSide(dc, random); break;
            case "ice": IceSide(dc, random); break;
            case "city": CitySide(dc, random); break;
            default: AlienSide(dc, random); break;
        }
        dc.Pop();
    }

    private void DockSide(DrawingContext dc, Random random)
    {
        Rect(dc, _palette.Dark, 0, 0, 116, Tile);
        for (var y = 8d; y < Tile; y += 21)
        {
            var x = random.Next(1, 37);
            Line(dc, _palette.Mid, x, y, x + random.Next(20, 57), y, 1);
            Line(dc, _palette.Ground, 19, y + 7, 94, y + 7, 1);
        }
        Rect(dc, _palette.Mid, 116, 0, 76, Tile);
        Line(dc, _palette.Light, 118, 0, 118, Tile, 3);
        for (var y = 0d; y < Tile; y += 32)
        {
            Line(dc, _palette.Dark, 120, y, Side, y);
            Rect(dc, _palette.Dark, 111, y + 8, 10, 15);
        }
        // Cargo roofs and their corrugation, a short pier, a crane footprint with boom and cable.
        Container(dc, 128, 23, 47, 111, _palette.AccentDim);
        Rect(dc, _palette.Dark, 33, 187, 117, 64, _palette.Light);
        for (var x = 38d; x < 146; x += 11) Line(dc, _palette.Mid, x, 191, x, 247, 3);
        Ellipse(dc, _palette.Dark, 43, 195, 5, 5, _palette.Light);
        Ellipse(dc, _palette.Dark, 43, 242, 5, 5, _palette.Light);
        Rect(dc, _palette.Dark, 132, 340, 43, 55, _palette.Light);
        Ellipse(dc, _palette.Mid, 153, 367, 13, 13, _palette.Accent);
        Rect(dc, _palette.AccentDim, 31, 359, 127, 13, _palette.Accent);
        for (var x = 38d; x < 144; x += 18)
        {
            Line(dc, _palette.Accent, x, 360, x + 13, 371);
            Line(dc, _palette.Dark, x, 371, x + 13, 360);
        }
        Line(dc, _palette.Light, 32, 366, 26, 405, 2);
        Rect(dc, _palette.Dark, 19, 403, 16, 11, _palette.Light);
        for (var y = 153d; y < Tile; y += 128) Vent(dc, 157, y, 19, 25);
    }

    private void ForestSide(DrawingContext dc, Random random)
    {
        // Top-down creek, canopy clusters, a broken temple footprint, mushrooms and flat fallen logs.
        for (var y = -25d; y < Tile; y += 45)
        {
            var x = 10 + Math.Sin(y / 78) * 12;
            Ellipse(dc, _palette.Dark, x, y, 35, 43);
            Line(dc, _palette.Mid, x - 8, y, x + 7, y - 14);
        }
        for (var i = 0; i < 10; i++)
            Tree(dc, 40 + random.NextDouble() * 105, i * 57 - 3 + random.Next(-12, 13), random.Next(20, 36), random);
        Ruin(dc, 59, 179);
        Rect(dc, _palette.AccentDim, 31, 392, 78, 12, _palette.Dark);
        for (var x = 38d; x < 107; x += 15) Line(dc, _palette.Dark, x, 393, x - 3, 403);
        for (var i = 0; i < 17; i++)
        {
            var x = random.Next(126, 183); var y = random.Next(0, 512);
            Line(dc, _palette.Mid, x, y - 7, x, y + 8, 2);
            Line(dc, _palette.Light, x - 6, y - 4, x, y + 2);
            Line(dc, _palette.Light, x + 7, y - 7, x, y);
            Line(dc, _palette.Mid, x - 8, y + 3, x, y + 7);
        }
    }

    private void DesertSide(DrawingContext dc, Random random)
    {
        for (var i = 0; i < 5; i++)
        {
            var y = i * 119 - 26;
            Shape(dc, _palette.Dark, [new(-8, y), new(75, y - 10), new(132, y + 33), new(123, y + 91), new(70, y + 112), new(-8, y + 82)]);
            Shape(dc, _palette.Mid, [new(-8, y + 12), new(70, y + 3), new(114, y + 38), new(106, y + 81), new(62, y + 95), new(-8, y + 71)]);
            Shape(dc, _palette.Light, [new(-5, y + 29), new(55, y + 17), new(88, y + 43), new(83, y + 68), new(52, y + 79), new(-5, y + 56)]);
            Line(dc, _palette.Ground, 17, y + 37, 55, y + 28, 2);
            Line(dc, _palette.Mid, 21, y + 61, 70, y + 52, 2);
        }
        for (var i = 0; i < 14; i++)
        {
            var x = random.Next(135, 181); var y = random.Next(5, 505);
            if (i % 3 == 0)
            {
                Line(dc, _palette.Dark, x, y - 14, x, y + 13, 5);
                Line(dc, _palette.Dark, x - 10, y - 7, x - 10, y + 3, 4);
                Line(dc, _palette.Dark, x - 10, y + 3, x + 9, y + 3, 4);
                Line(dc, _palette.Dark, x + 9, y + 3, x + 9, y - 11, 4);
            }
            else Rock(dc, x, y, random.Next(5, 11), _palette.Mid, random);
        }
        Ruin(dc, 97, 262);
        Ellipse(dc, _palette.Dark, 149, 458, 15, 22, _palette.AccentDim);
        Ellipse(dc, _palette.Mid, 149, 458, 7, 13);
    }

    private void IceSide(DrawingContext dc, Random random)
    {
        for (var i = 0; i < 22; i++)
        {
            var x = random.Next(0, 181); var y = random.Next(0, 512);
            Rock(dc, x, y, random.Next(10, 28), i % 2 == 0 ? _palette.Light : _palette.Mid, random);
        }
        Building(dc, 30, 43, 100, 135);
        Rect(dc, _palette.Dark, 51, 67, 58, 79, _palette.AccentDim);
        for (var y = 72d; y < 144; y += 9) Line(dc, _palette.Mid, 54, y, 105, y, 3);
        Rect(dc, _palette.Dark, 132, 94, 37, 25, _palette.AccentDim);
        Line(dc, _palette.Light, 147, 107, 172, 107, 3);
        Ellipse(dc, _palette.Dark, 79, 300, 41, 41, _palette.Light);
        Ellipse(dc, _palette.Mid, 79, 300, 29, 29, _palette.Accent);
        Line(dc, _palette.Dark, 53, 277, 104, 324, 4);
        Line(dc, _palette.Dark, 54, 322, 103, 277, 4);
        Ellipse(dc, _palette.Light, 79, 300, 7, 7);
        Container(dc, 107, 399, 53, 79, _palette.Mid);
        for (var y = 16d; y < Tile; y += 91)
        {
            Rect(dc, _palette.Dark, 176, y, 9, 28, _palette.AccentDim);
            Rect(dc, _palette.Accent, 178, y + 4, 5, 5);
        }
    }

    private void CitySide(DrawingContext dc, Random random)
    {
        for (var row = 0; row < 3; row++)
        {
            var x = random.Next(9, 22); var y = row * 178 + random.Next(8, 27);
            var roofWidth = random.Next(112, 130); var roofHeight = random.Next(124, 147);
            Building(dc, x, y, roofWidth, roofHeight);
            Rect(dc, _palette.AccentDim, x + 7, y + 8, roofWidth - 18, 4);
            var mirroredRoof = (row + _variant) % 2 == 0;
            var fanX = x + (mirroredRoof ? 89 : 33);
            var ventX = x + (mirroredRoof ? 16 : 70);
            Vent(dc, ventX, y + 28, 33, 39); Vent(dc, x + 66, y + 85, 37, 30);
            Ellipse(dc, _palette.Dark, fanX, y + 47, 21, 21, _palette.Light);
            Ellipse(dc, _palette.Mid, fanX, y + 47, 15, 15, _palette.Dark);
            Line(dc, _palette.Light, fanX - 10, y + 37, fanX + 10, y + 57, 2);
            Line(dc, _palette.Light, fanX - 10, y + 57, fanX + 10, y + 37, 2);
            Line(dc, _palette.Light, x + 19, y + 113, x + 54, y + 113, 2);
            Rect(dc, _palette.Accent, x + 48, y + 9, 23, 2);
        }
        Rect(dc, _palette.Dark, 153, 0, 39, Tile);
        for (var y = 0d; y < Tile; y += 32) Line(dc, _palette.Mid, 155, y, 190, y);
        for (var y = 18d; y < Tile; y += 83)
        {
            Rect(dc, _palette.Mid, 167, y + 6, 9, 24);
            Rect(dc, _palette.AccentDim, 160, y, 23, 9, _palette.Light);
            Rect(dc, _palette.Accent, 163, y + 3, 17, 2);
        }
    }

    private void AlienSide(DrawingContext dc, Random random)
    {
        for (var i = 0; i < 5; i++)
        {
            var y = i * 124 - 12;
            Ellipse(dc, _palette.Dark, 55, y + 50, 69, 54, _palette.Mid);
            Ellipse(dc, _palette.AccentDim, 55, y + 50, 51, 35);
            Ellipse(dc, _palette.Ground, 55, y + 50, 35, 19, _palette.Light);
            Line(dc, _palette.Mid, 12, y + 41, 63, y + 29);
        }
        for (var i = 0; i < 16; i++)
        {
            var x = random.Next(92, 178); var y = random.Next(0, 512); var size = random.Next(8, 20);
            Line(dc, _palette.Light, x, y, x + 4, y + size + 10, 3);
            Ellipse(dc, _palette.Dark, x, y, size + 3, size * .8, _palette.Mid);
            Ellipse(dc, _palette.Mid, x, y, size, size * .66, _palette.AccentDim);
            Ellipse(dc, _palette.Accent, x, y, size * .27, size * .20);
            Line(dc, _palette.AccentDim, x - size * .7, y + 1, x + size * .7, y + 1);
        }
        for (var y = 18d; y < Tile; y += 80)
        {
            Shape(dc, _palette.Mid, [new(173, y - 5), new(186, y + 12), new(181, y + 37), new(169, y + 19)], _palette.Light);
            Line(dc, _palette.AccentDim, 177, y + 5, 178, y + 23, 2);
        }
    }

    private void Container(DrawingContext dc, double x, double y, double w, double h, string color)
    {
        Rect(dc, color, x, y, w, h, _palette.Dark);
        Rect(dc, _palette.Light, x + 3, y + 3, w - 6, 3);
        for (var row = y + 11; row < y + h - 5; row += 9) Line(dc, _palette.Dark, x + 4, row, x + w - 4, row);
        Rect(dc, _palette.Dark, x + w * .3, y + h - 15, w * .4, 6);
    }
    private void Vent(DrawingContext dc, double x, double y, double w, double h)
    {
        Rect(dc, _palette.Dark, x, y, w, h, _palette.Light);
        for (var row = y + 4; row < y + h - 2; row += 5) Line(dc, _palette.Mid, x + 3, row, x + w - 3, row, 2);
    }
    private void Building(DrawingContext dc, double x, double y, double w, double h)
    {
        Rect(dc, _palette.Dark, x - 3, y - 3, w + 6, h + 6);
        Rect(dc, _palette.Mid, x, y, w, h, _palette.Light);
        Rect(dc, _palette.Ground, x + 6, y + 6, w - 12, h - 12, _palette.Dark);
        Line(dc, _palette.Mid, x + 8, y + h * .5, x + w - 8, y + h * .5, 2);
    }
    private void Ruin(DrawingContext dc, double x, double y)
    {
        Rect(dc, _palette.Dark, x - 29, y - 33, 70, 90);
        for (var i = 0; i < 5; i++)
        {
            Rect(dc, _palette.Mid, x - 31, y - 33 + i * 18, 14, 15, _palette.Light);
            if (i != 2) Rect(dc, _palette.Mid, x + 27, y - 33 + i * 18, 14, 15, _palette.Light);
            if (i != 3) Rect(dc, _palette.Mid, x - 13 + i * 10, y - 33, 13, 13, _palette.Light);
        }
        Rect(dc, _palette.Ground, x - 13, y - 13, 33, 49, _palette.Mid);
        Shape(dc, _palette.Light, [new(x + 12, y + 28), new(x + 30, y + 39), new(x + 18, y + 48), new(x + 5, y + 42)]);
    }
    private void Tree(DrawingContext dc, double x, double y, double radius, Random random)
    {
        Ellipse(dc, _palette.Dark, x, y, radius, radius * .95);
        for (var i = 0; i < 7; i++)
        {
            var angle = i * Math.PI * 2 / 7;
            var lx = x + Math.Cos(angle) * radius * .48; var ly = y + Math.Sin(angle) * radius * .48;
            Ellipse(dc, i % 2 == 0 ? _palette.Mid : _palette.Light, lx, ly, radius * .49, radius * .45);
            Line(dc, _palette.Dark, lx - 3, ly + 3, lx + 4, ly - 4);
        }
        Ellipse(dc, _palette.Mid, x, y, radius * .48, radius * .48);
    }
    private void Rock(DrawingContext dc, double x, double y, double radius, string color, Random random)
    {
        var points = new Point[7];
        for (var i = 0; i < points.Length; i++)
        {
            var angle = i * Math.PI * 2 / points.Length;
            var r = radius * (.65 + random.NextDouble() * .35);
            points[i] = new(x + Math.Cos(angle) * r, y + Math.Sin(angle) * r);
        }
        Shape(dc, color, points, _palette.Dark);
        Line(dc, _palette.Ground, x - radius * .3, y, x + radius * .2, y - radius * .3);
    }
}
