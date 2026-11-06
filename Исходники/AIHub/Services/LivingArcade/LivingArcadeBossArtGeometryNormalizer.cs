using System.IO;
using System.Windows;
using System.Windows.Media;
using AIHub.Models;
using Point = System.Windows.Point;

namespace AIHub.Services.LivingArcade;

/// <summary>Corrects representation mistakes in optional artwork, never combat or the shared visual contract.</summary>
internal static class LivingArcadeBossArtGeometryNormalizer
{
    internal static void Fit(LivingArcadeBossArt art)
    {
        art.GeometryFitted = false; art.PaletteDeduplicated = false; art.InactiveAnimationDefaulted = false;
        var visual = art.Visual;
        if (visual is null || visual.Mode is not ("library" or "vector") || visual.Frames is null
            || visual.Frames.Count > 3 || visual.Frames.Any(frame => frame is null || frame.Shapes is null || frame.Shapes.Count > 128)
            || visual.Decorations is null || visual.Decorations.Count > 16 || visual.Layers is null || visual.Layers.Count > 8
            || visual.Palette is null || visual.Palette.Count > 8) throw Invalid();
        if (visual.Animation is ("none" or "cycle") && visual.AnimationPeriod == 0)
        {
            visual.AnimationPeriod = 1.5;
            art.InactiveAnimationDefaulted = true;
        }
        var shapes = visual.Frames.SelectMany(frame => frame.Shapes).Concat(visual.Decorations).ToList();
        if (shapes.Count > 512 || shapes.Any(shape => shape is null)
            || shapes.Sum(shape => (long)(shape.Data?.Length ?? 0)) > 65_536) throw Invalid();
        foreach (var shape in shapes) _ = Bounds(shape);
        // Do this before fitting too: shrinking one enormous opaque plate must not turn it into an acceptable ornament.
        if (visual.Mode == "library") LivingArcadeBossArtPolicy.ValidateLibraryReadability(visual);
        try { art.GeometryFitted = LivingArcadeSceneryGeometryNormalizer.FitCanvas(shapes); }
        catch (InvalidDataException error) { throw Invalid(error); }
        foreach (var layer in visual.Layers)
        {
            if (layer is null || !Finite(layer.X) || !Finite(layer.Y) || !Finite(layer.Width) || !Finite(layer.Height)
                || layer.Width <= 0 || layer.Height <= 0 || !double.IsFinite(layer.Rotation)
                || layer.Rotation is < -30 or > 30) throw Invalid();
            var angle = layer.Rotation * Math.PI / 180;
            var width = Math.Abs(Math.Cos(angle)) * layer.Width + Math.Abs(Math.Sin(angle)) * layer.Height;
            var height = Math.Abs(Math.Sin(angle)) * layer.Width + Math.Abs(Math.Cos(angle)) * layer.Height;
            var scale = Math.Min(1, Math.Min(256 / width, 256 / height));
            var centerX = layer.X + layer.Width / 2; var centerY = layer.Y + layer.Height / 2;
            var newWidth = layer.Width * scale; var newHeight = layer.Height * scale;
            var halfWidth = Math.Min(128, width * scale / 2); var halfHeight = Math.Min(128, height * scale / 2);
            var x = Math.Clamp(centerX, halfWidth, 256 - halfWidth) - newWidth / 2;
            var y = Math.Clamp(centerY, halfHeight, 256 - halfHeight) - newHeight / 2;
            art.GeometryFitted |= Math.Abs(x - layer.X) > 1e-8 || Math.Abs(y - layer.Y) > 1e-8 || scale < 1;
            layer.X = x; layer.Y = y; layer.Width = newWidth; layer.Height = newHeight;
        }
        var unique = new List<LivingArcadePaletteSwap>();
        foreach (var swap in visual.Palette)
        {
            if (swap is null) throw Invalid();
            var previous = unique.FirstOrDefault(item => string.Equals(item.From, swap.From, StringComparison.OrdinalIgnoreCase));
            if (previous is null) unique.Add(swap);
            else if (string.Equals(previous.To, swap.To, StringComparison.OrdinalIgnoreCase)) art.PaletteDeduplicated = true;
            else throw LivingArcadeBossArtPolicy.DetailInvalid("Each palette source color may occur only once. Keep one consistent destination for each source; do not assign conflicting colors.");
        }
        visual.Palette = unique;
    }

    internal static Rect Bounds(LivingArcadeVisualShape shape)
    {
        if (!Finite(shape.X) || !Finite(shape.Y) || !Finite(shape.Width) || !Finite(shape.Height)
            || !double.IsFinite(shape.Opacity) || shape.Opacity is < 0 or > 1) throw Invalid();
        if (shape.Kind != "path")
        {
            if (shape.Kind is not ("rect" or "ellipse" or "line") || !Finite(shape.X + shape.Width)
                || !Finite(shape.Y + shape.Height) || shape.Kind != "line" && (shape.Width <= 0 || shape.Height <= 0)) throw Invalid();
            return new(new Point(shape.X, shape.Y), new Point(shape.X + shape.Width, shape.Y + shape.Height));
        }
        if (shape.Data is not { Length: > 0 and <= 4096 }) throw Invalid();
        try
        {
            var bounds = Geometry.Parse(shape.Data).Bounds;
            if (bounds.IsEmpty || !Finite(bounds.Left) || !Finite(bounds.Top) || !Finite(bounds.Right) || !Finite(bounds.Bottom)) throw Invalid();
            return bounds;
        }
        catch (Exception error) when (error is FormatException or ArgumentException or InvalidOperationException)
        { throw Invalid(error); }
    }

    private static bool Finite(double value) => double.IsFinite(value) && value is >= -512 and <= 512;
    private static InvalidDataException Invalid(Exception? inner = null)
    {
        var error = new InvalidDataException("Arcade.Ai.InvalidBossArt", inner);
        error.Data["ValidationDetail"] = "Use the 256x256 canvas: x,y are TOP LEFT, not center coordinates. Keep x+width and y+height inside 256. Library decorations must be small distinct fittings (4 or more), not a large plate covering the original body. For paths use real geometry coordinates and metadata x=y=width=height=0. Optional layers must be small and retain the detailed base silhouette.";
        return error;
    }
}
