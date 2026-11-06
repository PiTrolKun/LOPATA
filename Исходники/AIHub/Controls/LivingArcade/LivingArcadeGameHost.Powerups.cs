using System.Windows;
using System.Windows.Media;
using AIHub.Models;
using Point = System.Windows.Point;
using Pen = System.Windows.Media.Pen;

namespace AIHub.Controls.LivingArcade;

public sealed partial class LivingArcadeGameHost
{
    private void DrawPickupGlyph(DrawingContext dc, string id, double radius)
    {
        var negative = id.StartsWith("negative_", StringComparison.Ordinal);
        var kind = id == "time_scope_bonus" ? "time_scope"
            : id.StartsWith("bonus_", StringComparison.Ordinal) ? id[6..]
            : negative ? id[9..] : "repair";
        var r = radius * .6;
        var stroke = Math.Max(1.6, radius * .09);
        const string ink = "#FFFFFF";
        if (negative)
        {
            dc.DrawEllipse(null, new Pen(Brush("#FFDFE7"), stroke), new(), radius * 1.1, radius * 1.1);
            for (var i = 0; i < 4; i++)
            {
                var angle = i * Math.PI / 2;
                Line(dc, new(Math.Cos(angle) * radius * 1.1, Math.Sin(angle) * radius * 1.1),
                    new(Math.Cos(angle) * radius * 1.3, Math.Sin(angle) * radius * 1.3), stroke, "#FFDFE7");
            }
        }
        switch (kind)
        {
            case "time_scope":
                dc.DrawEllipse(null, new Pen(Brush(ink), stroke), new(), r, r);
                Line(dc, new(), new(0, -r * .7), stroke, ink);
                Line(dc, new(), new(r * .5, 0), stroke, ink);
                break;
            case "power":
                DrawGlyphPath(dc, [new(r * .2, -r), new(-r * .5, 0), new(r * .15, 0),
                    new(-r * .2, r), new(r * .55, -r * .15), new(-r * .1, -r * .15)], ink, true, stroke);
                break;
            case "rapid":
            case "cadence":
                for (var i = 0; i < (kind == "rapid" ? 3 : 2); i++)
                {
                    var y = -r * .7 + i * r * .65;
                    Line(dc, new(-r * .6, y + r * .35), new(0, y), stroke, ink);
                    Line(dc, new(0, y), new(r * .6, y + r * .35), stroke, ink);
                }
                break;
            case "spread":
                for (var i = -1; i <= 1; i++)
                    Line(dc, new(0, r * .7), new(i * r * .85, -r * .7), stroke, ink);
                break;
            case "shield":
            case "fragile":
                DrawGlyphPath(dc, [new(-r * .8, -r * .8), new(r * .8, -r * .8), new(r * .6, r * .25),
                    new(0, r), new(-r * .6, r * .25), new(-r * .8, -r * .8)], ink, false, stroke);
                if (kind == "fragile") DrawGlyphPath(dc,
                    [new(r * .1, -r * .7), new(-r * .25, -r * .1), new(r * .25, r * .1), new(0, r * .8)], ink, false, stroke);
                break;
            case "weapon":
                Line(dc, new(0, r), new(0, -r), stroke * 2, ink);
                Line(dc, new(-r * .55, -r * .4), new(0, -r), stroke, ink);
                Line(dc, new(r * .55, -r * .4), new(0, -r), stroke, ink);
                break;
            case "weapon_pistol":
                Fill(dc, -r * .75, -r * .5, r * 1.45, r * .45, ink);
                Fill(dc, -r * .55, -r * .1, r * .45, r * .9, ink);
                break;
            case "weapon_shotgun":
                Line(dc, new(-r * .75, r * .7), new(r * .75, -r * .8), stroke * 1.4, ink);
                Line(dc, new(-r * .55, r * .9), new(r * .95, -r * .6), stroke, ink);
                Line(dc, new(-r * .45, r * .55), new(-r * .8, r * .9), stroke * 2, ink);
                break;
            case "weapon_smg":
                Fill(dc, -r * .8, -r * .45, r * 1.6, r * .4, ink);
                Fill(dc, -r * .6, -r * .05, r * .3, r * .65, ink);
                Fill(dc, r * .1, -r * .05, r * .3, r * .8, ink);
                Line(dc, new(-r * .8, -r * .4), new(-r, r * .1), stroke, ink);
                break;
            case "weapon_laser":
                Line(dc, new(0, r * .85), new(0, -r), stroke * 1.3, ink);
                for (var i = -1; i <= 1; i++)
                    Line(dc, new(i * r * .5, r * .3), new(i * r * .5, -r * .3), stroke, ink);
                Line(dc, new(-r * .4, -r * .6), new(0, -r), stroke, ink);
                Line(dc, new(r * .4, -r * .6), new(0, -r), stroke, ink);
                break;
            case "weapon_rocket":
                DrawGlyphPath(dc, [new(0, -r), new(r * .45, -r * .4), new(r * .45, r * .6),
                    new(-r * .45, r * .6), new(-r * .45, -r * .4)], ink, false, stroke);
                Line(dc, new(-r * .4, r * .25), new(-r * .8, r * .85), stroke, ink);
                Line(dc, new(r * .4, r * .25), new(r * .8, r * .85), stroke, ink);
                Line(dc, new(0, r * .7), new(0, r), stroke, ink);
                break;
            case "helper":
                DrawGlyphPath(dc, [new(0, -r), new(r * .3, -r * .1), new(r, r * .5),
                    new(r * .3, r * .35), new(0, r * .7), new(-r * .3, r * .35),
                    new(-r, r * .5), new(-r * .3, -r * .1)], ink, false, stroke);
                Line(dc, new(-r * .65, -r * .75), new(-r * .65, -r * .3), stroke, ink);
                Line(dc, new(-r * .9, -r * .52), new(-r * .4, -r * .52), stroke, ink);
                break;
            case "vitality":
                DrawGlyphPath(dc, [new(0, -r * .3), new(-r * .4, -r * .8), new(-r * .85, -r * .5),
                    new(-r * .8, r * .15), new(0, r), new(r * .8, r * .15), new(r * .85, -r * .5),
                    new(r * .4, -r * .8), new(0, -r * .3)], ink, false, stroke);
                break;
            case "jam":
                Line(dc, new(-r * .6, -r * .6), new(r * .6, r * .6), stroke * 1.6, ink);
                Line(dc, new(r * .6, -r * .6), new(-r * .6, r * .6), stroke * 1.6, ink);
                break;
            case "drag":
                Line(dc, new(0, -r), new(0, r * .7), stroke, ink);
                Line(dc, new(-r * .6, r * .1), new(0, r * .8), stroke, ink);
                Line(dc, new(r * .6, r * .1), new(0, r * .8), stroke, ink);
                break;
            default:
                Fill(dc, -r * .75, -stroke, r * 1.5, stroke * 2, ink);
                Fill(dc, -stroke, -r * .75, stroke * 2, r * 1.5, ink);
                break;
        }
    }

    private void DrawGlyphPath(DrawingContext dc, Point[] points, string color, bool filled, double stroke)
    {
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(points[0], filled, filled);
            path.PolyLineTo(points.Skip(1).ToArray(), true, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(filled ? Brush(color) : null, filled ? null : new Pen(Brush(color), stroke), geometry);
    }

    private void DrawPlayerEffects(DrawingContext dc, Point player, double scale, LivingArcadeSnapshot snapshot)
    {
        if (snapshot.PowerupEffect.Length > 0 && snapshot.PowerupSeconds > 0)
        {
            var color = snapshot.PowerupEffect == "shield" ? "#66CCFF" : "#F8D16D";
            dc.PushOpacity(snapshot.PowerupSeconds < 2 ? .45 + .3 * Math.Sin(snapshot.Progress * 25) : .8);
            dc.DrawEllipse(null, new Pen(Brush(color), (snapshot.PowerupEffect == "shield" ? 3 : 1.5) * scale),
                player, 31 * scale, 31 * scale);
            dc.Pop();
        }
        if (snapshot.NegativeEffect.Length > 0 && snapshot.NegativeSeconds > 0)
        {
            dc.PushOpacity(.75);
            Line(dc, player + new Vector(-29 * scale, 25 * scale), player + new Vector(29 * scale, 25 * scale), 3 * scale, "#FF757F");
            dc.Pop();
        }
        var milestone = snapshot.CharacterLevel / 5;
        var orbitals = Math.Clamp(milestone, 0, 3);
        var colorIndex = Math.Max(0, milestone - 1) % 5;
        var orbitalColor = colorIndex switch
        { 0 => "#F8D16D", 1 => "#66CCFF", 2 => "#C792EA", 3 => "#72F1B8", _ => "#FFB34D" };
        var style = Math.Max(0, milestone - 1) % 3;
        for (var i = 0; i < orbitals; i++)
        {
            var angle = snapshot.Progress * 2.1 + i * Math.PI * 2 / orbitals;
            var point = player + new Vector(Math.Cos(angle) * 43 * scale, Math.Sin(angle) * 30 * scale);
            dc.PushOpacity(.22); Disc(dc, point, 8 * scale, orbitalColor); dc.Pop();
            if (style == 0) Disc(dc, point, 3 * scale, orbitalColor);
            else if (style == 1) dc.DrawEllipse(null, new Pen(Brush(orbitalColor), 1.6 * scale), point, 4 * scale, 4 * scale);
            else
            {
                dc.PushTransform(new TranslateTransform(point.X, point.Y));
                dc.PushTransform(new RotateTransform(45));
                Fill(dc, -3 * scale, -3 * scale, 6 * scale, 6 * scale, orbitalColor);
                dc.Pop(); dc.Pop();
            }
        }
    }
}
