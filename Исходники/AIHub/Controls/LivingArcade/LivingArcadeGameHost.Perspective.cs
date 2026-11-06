using System.Windows;
using System.Windows.Media;
using AIHub.Models;
using AIHub.Services.LivingArcade;
using Point = System.Windows.Point;
using Pen = System.Windows.Media.Pen;

namespace AIHub.Controls.LivingArcade;

public sealed partial class LivingArcadeGameHost
{
    private readonly List<SceneActor> _sceneActors = new(256);
    private readonly record struct SceneActor(int Kind, LivingArcadeEntityView? Entity, double Y, int Order);
    private sealed class SceneActorOrder : IComparer<SceneActor>
    {
        public static readonly SceneActorOrder Instance = new();
        public int Compare(SceneActor a, SceneActor b)
        { var depth = a.Y.CompareTo(b.Y); return depth != 0 ? depth : a.Order.CompareTo(b.Order); }
    }

    private Point ProjectWorld(double x, double y, double left, double width, double height) =>
        ViewMode == LivingArcadeViewMode.TopDown ? World(x, y, left, width, height)
            : new LivingArcadeProjection(true, left, width, height).Project(x, y);
    private double DepthScale(double y) => new LivingArcadeProjection(
        ViewMode == LivingArcadeViewMode.Perspective, 0, 1, 1).ScaleAt(y);

    private void DrawProjectedShield(DrawingContext dc, LivingArcadeEntityView entity,
        double left, double width, double height, double radius)
    {
        var camera = new LivingArcadeProjection(true, left, width, height);
        var worldRadius = radius / (width * camera.ScaleAt(entity.Y)) * 1.18;
        var opening = Math.Clamp(entity.WeakSpotWidthDegrees, 1, 360);
        var sweep = 360 - opening;
        var segments = Math.Max(1, (int)Math.Ceiling(sweep / 5));
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            for (var i = 0; i <= segments; i++)
            {
                var angle = (entity.WeakSpotAngleDegrees + opening / 2 + sweep * i / segments) * Math.PI / 180;
                var point = camera.Project(entity.X + Math.Sin(angle) * worldRadius, entity.Y - Math.Cos(angle) * worldRadius);
                if (i == 0) path.BeginFigure(point, false, false);
                else path.LineTo(point, true, false);
            }
        }
        geometry.Freeze();
        dc.DrawGeometry(null, new Pen(Brush("#76E9FF"), Math.Max(2, radius * .07)), geometry);
    }

    private void DrawSceneActors(DrawingContext dc, LivingArcadeSnapshot snapshot, double left, double width, double height)
    {
        if (ViewMode == LivingArcadeViewMode.TopDown)
        {
            foreach (var entity in snapshot.Pickups) DrawEntity(dc, entity, left, width, height, true);
            foreach (var entity in snapshot.Enemies) DrawEntity(dc, entity, left, width, height, false);
            foreach (var companion in snapshot.Companions) DrawCompanion(dc, companion, left, width, height);
            DrawSceneFog(dc, snapshot, left, width, height);
            DrawPlayer(dc, snapshot, left, width, height);
            return;
        }

        // Reuse a bounded-by-simulation queue: nearby actors cover distant ones, including the hero.
        _sceneActors.Clear();
        foreach (var entity in snapshot.Pickups) _sceneActors.Add(new(0, entity, entity.Y, _sceneActors.Count));
        foreach (var entity in snapshot.Enemies) _sceneActors.Add(new(1, entity, entity.Y, _sceneActors.Count));
        foreach (var entity in snapshot.Companions) _sceneActors.Add(new(2, entity, entity.Y, _sceneActors.Count));
        _sceneActors.Add(new(3, null, snapshot.PlayerY, _sceneActors.Count));
        _sceneActors.Sort(SceneActorOrder.Instance);
        foreach (var actor in _sceneActors)
        {
            if (actor.Kind == 3) DrawPlayer(dc, snapshot, left, width, height);
            else if (actor.Kind == 2) DrawCompanion(dc, actor.Entity!, left, width, height);
            else DrawEntity(dc, actor.Entity!, left, width, height, actor.Kind == 0);
        }
        _sceneActors.Clear();
        DrawSceneFog(dc, snapshot, left, width, height);
    }

    private void DrawSceneFog(DrawingContext dc, LivingArcadeSnapshot snapshot, double left, double width, double height)
    {
        if (snapshot.ActiveEffect != "fog") return;
        dc.PushOpacity(.56);
        if (ViewMode == LivingArcadeViewMode.TopDown) Fill(dc, left, 0, width, height * .77, "#243746");
        else
        {
            var camera = new LivingArcadeProjection(true, left, width, height);
            dc.DrawGeometry(Brush("#243746"), null, Polygon([
                camera.Project(0, -.2), camera.Project(1, -.2), camera.Project(1, .77), camera.Project(0, .77)]));
        }
        dc.Pop();
    }

    private void DrawPlayer(DrawingContext dc, LivingArcadeSnapshot snapshot, double left, double width, double height)
    {
        var player = ProjectWorld(snapshot.PlayerX, snapshot.PlayerY, left, width, height);
        var scale = Math.Clamp(Math.Min(width / 640, height / 600), .55, 2) * DepthScale(snapshot.PlayerY);
        if (snapshot.SuperFlash > 0)
        {
            var strength = Math.Clamp(snapshot.SuperFlash, 0, 1);
            dc.PushOpacity(strength * .32);
            Disc(dc, player, ((1 - strength) * width * .43 + 30) * DepthScale(snapshot.PlayerY), "#56D0EB");
            dc.Pop();
        }
        var aim = new LivingArcadeProjection(ViewMode == LivingArcadeViewMode.Perspective,
            left, width, height).AimAngle(snapshot.PlayerX, snapshot.PlayerY, snapshot.AngleRadians);
        DrawShovel(dc, player, scale, aim, snapshot.IsDefeated);
        DrawPlayerEffects(dc, player, scale, snapshot);
        if (snapshot.BonusFeedbackSeconds <= 0) return;
        var glow = Math.Min(1, snapshot.BonusFeedbackSeconds);
        dc.PushOpacity(glow * .7);
        dc.DrawEllipse(null, new Pen(Brush(snapshot.RecentBonus?.IsNegative == true ? "#FF757F"
            : snapshot.RecentBonus?.TimeScopeChanged == true ? "#66CCFF" : "#72F1B8"), 3 * scale),
            player, 30 * scale, 30 * scale);
        dc.Pop();
    }
}
