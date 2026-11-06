using System.Windows.Media;
using Point = System.Windows.Point;

namespace AIHub.Controls.LivingArcade;

public sealed partial class LivingArcadeBackground
{
    private void DrawRoad(DrawingContext dc, Random random)
    {
        Rect(dc, _palette.Floor, 0, 0, Road, Tile);
        switch (SceneKind)
        {
            case "harbor": DockFloor(dc); break;
            case "forest": ForestFloor(dc, random); break;
            case "desert": DesertFloor(dc, random); break;
            case "ice": IceFloor(dc, random); break;
            case "city": CityFloor(dc); break;
            default: AlienFloor(dc, random); break;
        }
        // Tiny low-contrast grain ties broad floor shapes together without resembling projectiles or pickups.
        for (var i = 0; i < 155; i++)
        {
            var x = random.NextDouble() * Road; var y = random.NextDouble() * Tile;
            Rect(dc, i % 4 == 0 ? _palette.Seam : _palette.Mark, x, y, random.Next(1, 4), 1);
        }
    }

    private void DockFloor(DrawingContext dc)
    {
        for (var row = 0; row < 8; row++)
        for (var column = 0; column < 5; column++)
        {
            var x = column * 128; var y = row * 64;
            Rect(dc, _palette.Floor, x + 2, y + 2, 124, 60, _palette.Seam);
            Line(dc, _palette.Mark, x + 8, y + 5, x + 120, y + 5);
            foreach (var dx in new[] { 9d, 118d })
            foreach (var dy in new[] { 11d, 53d }) Ellipse(dc, _palette.Mark, x + dx, y + dy, 1.3, 1.3);
            if ((row + column + _variant) % 7 == 0)
            {
                Rect(dc, _palette.Seam, x + 38, y + 24, 46, 15, _palette.Mark);
                for (var grate = 0; grate < 5; grate++) Line(dc, _palette.Floor, x + 43 + grate * 8, y + 27, x + 43 + grate * 8, y + 36, 2);
            }
        }
        for (var y = 0d; y < Tile; y += 32)
        {
            Rect(dc, _palette.AccentDim, 8, y, 7, 19);
            Rect(dc, _palette.AccentDim, Road - 15, y + 13, 7, 19);
        }
        Line(dc, _palette.Mark, 25, 0, 25, Tile, 2);
        Line(dc, _palette.Mark, Road - 25, 0, Road - 25, Tile, 2);
    }

    private void ForestFloor(DrawingContext dc, Random random)
    {
        // Worn flagstones are flat floor, never collidable scenery.
        for (var row = 0; row < 9; row++)
        for (var column = 0; column < 8; column++)
        {
            var x = column * 82 - (row % 2) * 34; var y = row * 62 - 20;
            var inset = random.Next(3, 9);
            Shape(dc, (row + column) % 3 == 0 ? _palette.Seam : _palette.Floor,
                [new(x + inset, y + 3), new(x + 74, y + 7), new(x + 79, y + 49),
                 new(x + 48, y + 58), new(x + 4, y + 51)], _palette.Mark);
        }
        for (var i = 0; i < 45; i++)
        {
            var x = i % 2 == 0 ? random.Next(4, 47) : Road - random.Next(4, 47);
            var y = random.NextDouble() * Tile;
            Ellipse(dc, _palette.Seam, x, y, random.Next(6, 17), random.Next(4, 11));
            Line(dc, _palette.Mark, x - 5, y, x + 6, y - 4);
        }
        for (var y = 15d; y < Tile; y += 90)
        {
            Line(dc, _palette.Mark, 16, y, 26, y + 37, 2);
            Line(dc, _palette.Mark, Road - 20, y + 30, Road - 13, y + 63, 2);
        }
    }

    private void DesertFloor(DrawingContext dc, Random random)
    {
        for (var y = -20d; y < Tile + 30; y += 30)
        {
            var phase = random.NextDouble() * Math.PI;
            for (var x = 0d; x < Road; x += 40)
            {
                var a = y + Math.Sin(x / 105 + phase) * 9;
                var b = y + Math.Sin((x + 40) / 105 + phase) * 9;
                Line(dc, _palette.Seam, x, a, x + 40, b);
            }
        }
        foreach (var track in new[] { 119d, 132d, 507d, 520d })
        for (var y = 0d; y < Tile; y += 13) Rect(dc, _palette.Mark, track, y, 4, 5);
        for (var i = 0; i < 36; i++)
        {
            var x = random.NextDouble() * Road; var y = random.NextDouble() * Tile;
            Shape(dc, _palette.Seam, [new(x, y), new(x + 6, y - 2), new(x + 11, y + 3), new(x + 4, y + 5)]);
        }
        for (var y = 22d; y < Tile; y += 77)
        {
            Line(dc, _palette.AccentDim, 6, y, 25, y + 5, 2);
            Line(dc, _palette.AccentDim, Road - 27, y + 37, Road - 7, y + 31, 2);
        }
    }

    private void IceFloor(DrawingContext dc, Random random)
    {
        for (var i = 0; i < 17; i++)
        {
            var x = random.NextDouble() * Road; var y = random.NextDouble() * Tile;
            var length = random.Next(36, 90);
            var a = new Point(x - length * .6, y - 17);
            var b = new Point(x - length * .2, y + 4);
            var c = new Point(x + length * .2, y - 6);
            var d = new Point(x + length * .5, y + 23);
            Line(dc, _palette.Mark, a.X, a.Y, b.X, b.Y);
            Line(dc, _palette.Mark, b.X, b.Y, c.X, c.Y);
            Line(dc, _palette.Mark, c.X, c.Y, d.X, d.Y);
            Line(dc, _palette.Seam, b.X, b.Y, b.X - 5, b.Y + 20);
        }
        for (var y = 0d; y < Tile; y += 64)
        {
            Rect(dc, _palette.Seam, 6, y + 4, 25, 48, _palette.Mark);
            Rect(dc, _palette.Seam, Road - 31, y + 4, 25, 48, _palette.Mark);
            Line(dc, _palette.AccentDim, 10, y + 9, 27, y + 9);
            Line(dc, _palette.AccentDim, Road - 27, y + 45, Road - 10, y + 45);
        }
        for (var i = 0; i < 42; i++)
        {
            var x = i % 2 == 0 ? random.Next(4, 42) : Road - random.Next(4, 42);
            Rect(dc, _palette.Mark, x, random.NextDouble() * Tile, random.Next(4, 14), 2);
        }
    }

    private void CityFloor(DrawingContext dc)
    {
        var pavingOffset = Math.Abs(_variant % 3) * 17;
        for (var y = 0d; y < Tile; y += 64)
        {
            for (var x = -80d + pavingOffset; x < Road; x += 80)
            {
                Rect(dc, _palette.Floor, x + 1, y + 1, 78, 62, _palette.Seam);
                if ((((int)x / 80) + ((int)y / 64) + _variant) % 5 == 0)
                    Line(dc, _palette.Mark, x + 8, y + 8, x + 40, y + 8);
            }
            Rect(dc, _palette.AccentDim, 9, y + 5, 7, 33);
            Rect(dc, _palette.AccentDim, Road - 16, y + 29, 7, 30);
            Rect(dc, _palette.Mark, Road / 2 - 1, y + 10, 2, 22);
        }
        foreach (var x in new[] { 31d, Road - 50 })
        {
            Rect(dc, _palette.Dark, x, 183, 19, 98, _palette.Mark);
            for (var y = 188d; y < 277; y += 7) Line(dc, _palette.Seam, x + 3, y, x + 16, y);
        }
        for (var x = 58d; x < Road - 60; x += 35) Rect(dc, _palette.Seam, x, 430 + pavingOffset, 23, 7);
    }

    private void AlienFloor(DrawingContext dc, Random random)
    {
        for (var row = -1; row < 8; row++)
        for (var column = -1; column < 9; column++)
        {
            var x = column * 90 + (row % 2) * 45; var y = row * 74;
            Shape(dc, _palette.Floor,
                [new(x, y - 35), new(x + 37, y - 19), new(x + 39, y + 19),
                 new(x, y + 35), new(x - 39, y + 19), new(x - 38, y - 18)], _palette.Seam);
            if ((row + column + _variant) % 4 == 0)
            {
                Line(dc, _palette.Mark, x - 17, y, x - 5, y - 11);
                Line(dc, _palette.Mark, x - 5, y - 11, x + 19, y + 2);
                Ellipse(dc, _palette.Seam, x + 19, y + 2, 3, 2);
            }
        }
        for (var i = 0; i < 22; i++)
        {
            var x = i % 2 == 0 ? random.Next(8, 40) : Road - random.Next(8, 40);
            var y = random.NextDouble() * Tile;
            Ellipse(dc, _palette.AccentDim, x, y, random.Next(4, 9), random.Next(7, 15));
            Ellipse(dc, _palette.Seam, x, y, 2, 5);
        }
    }
}
