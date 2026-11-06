using System.Globalization;
using System.IO;
using System.Windows.Media;
using AIHub.Models;

namespace AIHub.Services.LivingArcade;

/// <summary>Limits model art to finite local geometry; original physics and collision size stay authoritative.</summary>
public static class LivingArcadeVisualPolicy
{
    public static void Validate(LivingArcadeVisual? visual)
    {
        if (visual is null) return;
        if (visual.Mode is not ("procedural" or "library" or "vector")
            || visual.Animation is not ("none" or "bob" or "sway" or "pulse" or "cycle")
            || !Range(visual.AnimationAmount, 0, 15) || !Range(visual.AnimationPeriod, .5, 4)
            || visual.Palette is null || visual.Palette.Count > 8
            || visual.Frames is null || visual.Frames.Count > 8
            || visual.Decorations is null || visual.Decorations.Count > 16
            || visual.Layers is null || visual.Layers.Count > 8) throw Invalid();
        if (visual.Mode == "library" && !AssetId(visual.AssetId)) throw Invalid();
        if (visual.Mode != "library" && visual.AssetId is not null) throw Invalid();
        if (visual.Mode == "vector" && visual.Frames.Count == 0) throw Invalid();
        if (visual.Mode != "vector" && visual.Frames.Count != 0) throw Invalid();
        var colors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var swap in visual.Palette)
            if (swap is null || !Color(swap.From) || !Color(swap.To) || !colors.Add(swap.From)) throw Invalid();
        foreach (var layer in visual.Layers)
        {
            if (layer is null || !AssetId(layer.AssetId) || !Range(layer.X, 0, 256) || !Range(layer.Y, 0, 256)
                || !Range(layer.Width, .01, 256) || !Range(layer.Height, .01, 256)
                || !Range(layer.Rotation, -30, 30) || !Range(layer.Opacity, 0, 1)
                || layer.Animation is not ("none" or "bob" or "sway" or "pulse" or "cycle")) throw Invalid();
            var angle = layer.Rotation * Math.PI / 180;
            var halfWidth = (Math.Abs(Math.Cos(angle)) * layer.Width + Math.Abs(Math.Sin(angle)) * layer.Height) / 2;
            var halfHeight = (Math.Abs(Math.Sin(angle)) * layer.Width + Math.Abs(Math.Cos(angle)) * layer.Height) / 2;
            var centerX = layer.X + layer.Width / 2; var centerY = layer.Y + layer.Height / 2;
            if (!Range(centerX - halfWidth, -1e-8, 256) || !Range(centerY - halfHeight, -1e-8, 256)
                || !Range(centerX + halfWidth, 0, 256 + 1e-8) || !Range(centerY + halfHeight, 0, 256 + 1e-8)) throw Invalid();
        }
        var shapes = new List<LivingArcadeVisualShape>(visual.Decorations);
        foreach (var frame in visual.Frames)
        {
            if (frame is null || !Range(frame.Seconds, .04, 2) || frame.Shapes is null
                || frame.Shapes.Count is < 1 or > 128) throw Invalid();
            shapes.AddRange(frame.Shapes);
        }
        if (shapes.Count > 512) throw Invalid();
        var pathLength = 0;
        foreach (var shape in shapes)
        {
            if (shape is null || shape.Kind is not ("rect" or "ellipse" or "line" or "path")
                || !(Color(shape.Fill) || shape.Fill == "none")
                || !(Color(shape.Stroke) || shape.Stroke == "none")
                || !Range(shape.Opacity, 0, 1) || !Range(shape.StrokeWidth, 0, 16)
                || !Range(shape.X, 0, 256) || !Range(shape.Y, 0, 256)
                || !Range(shape.Width, -256, 256) || !Range(shape.Height, -256, 256)) throw Invalid();
            if (shape.Kind == "path")
            {
                if (shape.Data is null || shape.Data.Length is < 1 or > 4096) throw Invalid();
                pathLength += shape.Data.Length;
                if (pathLength > 65_536 || shape.Data.Any(c => !PathCharacter(c))) throw Invalid();
                try
                {
                    var bounds = Geometry.Parse(shape.Data).Bounds;
                    if (bounds.IsEmpty || !Range(bounds.Left, 0, 256) || !Range(bounds.Top, 0, 256)
                        || !Range(bounds.Right, 0, 256) || !Range(bounds.Bottom, 0, 256)) throw Invalid();
                }
                catch (Exception e) when (e is FormatException or ArgumentException or InvalidOperationException)
                { throw Invalid(); }
            }
            else
            {
                if (shape.Data is not null || !Range(shape.X + shape.Width, 0, 256)
                    || !Range(shape.Y + shape.Height, 0, 256)) throw Invalid();
                if (shape.Kind != "line" && (shape.Width <= 0 || shape.Height <= 0)) throw Invalid();
            }
        }
    }

    private static bool PathCharacter(char value) => char.IsAsciiDigit(value) || char.IsWhiteSpace(value)
        || "MmLlHhVvCcSsQqTtAaZzEe.,+-".Contains(value);
    private static bool AssetId(string? id) => id is { Length: > 0 and <= 160 }
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
    private static bool Range(double value, double min, double max) => double.IsFinite(value) && value >= min && value <= max;
    private static bool Color(string? value) => value is { Length: 7 } && value[0] == '#'
        && uint.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _);
    private static InvalidDataException Invalid() => new("Рисунок модели содержит неподдерживаемые или слишком большие элементы.");
}
