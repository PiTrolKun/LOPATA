using System.Windows;
using System.Windows.Media;
using AIHub.Models;
using Point = System.Windows.Point;
using Pen = System.Windows.Media.Pen;

namespace AIHub.Controls.LivingArcade;

public sealed partial class LivingArcadeGameHost
{
    private void DrawProjectile(DrawingContext dc, LivingArcadeEntityView shot, double left, double width, double height)
    {
        var center = ProjectWorld(shot.X, shot.Y, left, width, height);
        var radius = Math.Max(ViewMode == AIHub.Services.LivingArcade.LivingArcadeViewMode.Perspective ? 1.5 : 2.5,
            shot.Radius * width * DepthScale(shot.Y));
        var color = shot.Color;
        dc.PushOpacity(.18); Disc(dc, center, radius * 2, color); dc.Pop();
        dc.PushTransform(new TranslateTransform(center.X, center.Y));
        var perspective = ViewMode == AIHub.Services.LivingArcade.LivingArcadeViewMode.Perspective;
        if (perspective) dc.PushTransform(new RotateTransform(new LivingArcadeProjection(true, left, width, height)
            .AimAngle(shot.X, shot.Y, shot.HeadingRadians ?? 0) * 180 / Math.PI));
        // Kind is simulation data, not a weapon inferred from its colour: model rewards may change that colour.
        switch (shot.Hostile ? "hostile" : shot.Id)
        {
            case "laser":
                Line(dc, new(0, radius * 2.8), new(0, -radius * 3.3), radius * 1.5, color);
                Line(dc, new(0, radius * 2.6), new(0, -radius * 3.1), Math.Max(1, radius * .45), "#FFFFFF");
                break;
            case "rockets":
                DrawGlyphPath(dc, [new(-radius * .55, radius), new(0, radius * 3),
                    new(radius * .55, radius)], "#FFB34D", true, 1);
                DrawGlyphPath(dc, [new(0, -radius * 2), new(radius * .75, -radius * .3),
                    new(radius * .75, radius), new(-radius * .75, radius), new(-radius * .75, -radius * .3)], color, true, 1);
                Line(dc, new(0, -radius), new(0, radius * .6), Math.Max(1, radius * .3), "#FFFFFF");
                break;
            case "shotgun":
                DrawGlyphPath(dc, [new(0, -radius * 1.5), new(radius, 0), new(0, radius * 1.5),
                    new(-radius, 0)], color, true, 1);
                break;
            case "smg":
                Line(dc, new(0, radius * 1.8), new(0, -radius * 1.8), radius * .9, color);
                Disc(dc, new(0, -radius * 1.5), radius * .4, "#FFFFFF");
                break;
            case "helper_shot":
                DrawGlyphPath(dc, [new(0, -radius * 1.4), new(radius * 1.2, 0), new(0, radius * 1.4),
                    new(-radius * 1.2, 0), new(0, -radius * 1.4)], color, false, Math.Max(1.2, radius * .5));
                Disc(dc, new(), radius * .35, "#FFFFFF");
                break;
            case "pistol":
                Line(dc, new(0, radius * 1.8), new(), Math.Max(1.2, radius * .7), color);
                Disc(dc, new(), radius, color);
                break;
            default:
                Disc(dc, new(), radius, color);
                break;
        }
        if (shot.Returning) dc.DrawEllipse(null, new Pen(Brush("#FFFFFF"), 1.2), new(), radius * 1.7, radius * 1.7);
        if (perspective) dc.Pop();
        dc.Pop();
    }

    private void DrawCompanion(DrawingContext dc, LivingArcadeEntityView companion, double left, double width, double height)
    {
        if (companion.HealthFraction <= 0 || companion.Y is < -.2 or > 1.2) return;
        var center = ProjectWorld(companion.X, companion.Y, left, width, height);
        var depth = DepthScale(companion.Y);
        var radius = Math.Max(ViewMode == AIHub.Services.LivingArcade.LivingArcadeViewMode.Perspective ? 5 : 9,
            companion.Radius * width * depth);
        var color = companion.Flash > 0 ? "#FFFFFF" : companion.Color;
        dc.PushOpacity(.25); Disc(dc, center + new Vector(1, radius * .4), radius * 1.2, "#000000"); dc.Pop();
        dc.PushTransform(new TranslateTransform(center.X, center.Y));
        // A winged ship with its own health bar is distinct from the tiny decorative milestone orbitals.
        DrawGlyphPath(dc, [new(0, -radius), new(radius * .35, -radius * .1), new(radius, radius * .55),
            new(radius * .4, radius * .45), new(radius * .25, radius), new(-radius * .25, radius),
            new(-radius * .4, radius * .45), new(-radius, radius * .55), new(-radius * .35, -radius * .1)], color, true, 1);
        Fill(dc, -radius * .15, -radius * .55, radius * .3, radius * .55, "#142635");
        Line(dc, new(0, -radius * .85), new(0, -radius * 1.25), Math.Max(1.5, radius * .15), "#FFFFFF");
        dc.PushOpacity(.55 + Math.Sin(companion.Age * 19) * .15);
        DrawGlyphPath(dc, [new(-radius * .14, radius * .9), new(0, radius * 1.4), new(radius * .14, radius * .9)],
            "#66CCFF", true, 1); dc.Pop();
        dc.Pop();
        Fill(dc, center.X - radius, center.Y - radius - 8 * depth, radius * 2, Math.Max(2, 4 * depth), "#243447");
        Fill(dc, center.X - radius, center.Y - radius - 8 * depth, radius * 2 * Math.Clamp(companion.HealthFraction, 0, 1), Math.Max(2, 4 * depth), "#66CCFF");
    }
}
