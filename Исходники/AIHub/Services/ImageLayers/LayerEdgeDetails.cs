using System.Drawing;
using System.IO;
using System.Text.Json;
using SkiaSharp;

namespace AIHub.Services.ImageLayers;

internal static class LayerEdgeDetails
{
    private static int Reach(Rectangle geometry) => Math.Clamp(10 + Math.Min(geometry.Width, geometry.Height) / 8, 18, 64);

    internal static Rectangle WorkingArea(LayerSource source, LayerMask mask)
    {
        if (!mask.Any) return Rectangle.Empty;
        var bounds = mask.Bounds(source.Width);
        var padding = Reach(bounds) + 16;
        return Rectangle.Intersect(source.Frame, Rectangle.Inflate(bounds, padding, padding));
    }

    internal static bool Valid(LayerAnalysisInput input, LayerDocument work) =>
        input.Target is Guid target && WorkingArea(work.Source, work.Aggregate(target)).Contains(input.Area)
        && (input.Extra?.Strokes is not { } strokes || strokes.All(stroke => stroke is { Points.Length: > 0 }
            && stroke.Points.All(point => point is not null && input.Area.Contains(point.X, point.Y))));

    internal static async Task<(Dictionary<Guid, ImageLayer> Plan, OutputModelUse[] Models)> PlanAsync(
        LayerDocument work, LayerAnalysisInput input, IProgress<LayerAnalysisProgress> progress, CancellationToken token)
    {
        progress.Report(new("EdgeDetailsProgress"));
        var id = input.Target!.Value;
        var shape = work.Aggregate(id);
        var oldMatte = work.ActiveMatte(id);
        var workArea = WorkingArea(work.Source, shape);
        var crop = oldMatte is { } baseline ? Rectangle.Union(workArea, baseline.Reference.Bounds) : workArea;
        var details = await LayerToolWorker.EdgeDetailsAsync(work.Source, crop, input.Area, shape,
            Reach(shape.Bounds(work.Source.Width)), oldMatte, input.Extra?.Strokes ?? [], token);
        token.ThrowIfCancellationRequested();

        // Keep ownership and history of every other layer. Fine alpha detail can
        // overlap the neighbor's geometry without transferring its opaque pixels.
        var layer = work.Layers[id];
        var additions = details.Mask.Difference(shape);
        var taken = work.Merge(work.Layers.Values.Where(other => other.Id != id).Select(work.Direct));
        var updated = work.Direct(layer).Intersect(details.Mask).Union(additions.Difference(taken));
        var plan = new Dictionary<Guid, ImageLayer>(work.Layers) { [id] = layer with { Mask = work.Remember(updated) } };
        var geometry = work.Aggregate(id, plan);
        if (!geometry.Any) throw new InvalidDataException("ImageLayers.NoSoftEdge");
        plan[id] = plan[id] with { Matte = work.AddMatte(details.Matte, geometry, crop) };
        var previousModels = work.LayerHistory(id).LastOrDefault(step => step.Models.Length > 0)?.Models ?? [];
        return (plan, [.. previousModels.Concat([LayerToolWorker.PyMatting, LayerToolWorker.EdgeRidges]).Distinct()]);
    }
}

internal static partial class LayerToolWorker
{
    internal static OutputModelUse EdgeRidges => new(LayerRasterRuntime.LicenseId, "OpenCV", "4.10.0", "thin edge detail");

    internal static Task<(LayerMask Mask, LayerMatte Matte)> EdgeDetailsAsync(LayerSource source, Rectangle crop, Rectangle area,
        LayerMask mask, int reach, (LayerMatteReference Reference, LayerMatte Image)? existing, LayerEdgeStroke[] strokes,
        CancellationToken token) => InWorkspaceAsync(async folder =>
    {
        var rgb = source.Encode(crop, forModel: false);
        var previous = existing?.Image.Png ?? RenderExistingContour(rgb, crop, source.Width, mask, token);
        var localMask = LayerMask.Build(checked(crop.Width * crop.Height), bits =>
        {
            for (var y = 0; y < crop.Height; y++)
            {
                token.ThrowIfCancellationRequested();
                for (var x = 0; x < crop.Width; x++)
                    bits[y * crop.Width + x] = mask[(crop.Top + y) * source.Width + crop.Left + x];
            }
        });
        var bounds = existing?.Reference.Bounds ?? crop;
        int[] Point(int x, int y) => [x - crop.X, y - crop.Y];
        var config = new
        {
            area = new[] { area.Left - crop.Left, area.Top - crop.Top, area.Right - crop.Left, area.Bottom - crop.Top },
            reach,
            baseline = Point(bounds.Left, bounds.Top),
            strokes = strokes.Select(stroke => new object[] { stroke.Keep, stroke.Points.Select(point => Point(point.X, point.Y)) })
        };
        await File.WriteAllBytesAsync(Path.Combine(folder, "source.png"), rgb, token);
        await File.WriteAllBytesAsync(Path.Combine(folder, "before.png"), previous, token);
        await File.WriteAllBytesAsync(Path.Combine(folder, "input.bits"), localMask.Pack(), token);
        await File.WriteAllTextAsync(Path.Combine(folder, "edge.json"), JsonSerializer.Serialize(config), token);
        await File.WriteAllTextAsync(Path.Combine(folder, "edge_details.py"), await CodeAsync("EdgeDetails", token), token);
        await RunPortraitAsync(folder, LayerAdditionalMethods.EdgeDetails, token);
        var masks = await LayerRasterBatch.ReadMasksAsync(source, crop, folder, 1, token);
        return (masks[0], await ReadMatteAsync(folder, token));
    });

    private static byte[] RenderExistingContour(byte[] source, Rectangle crop, int width, LayerMask mask, CancellationToken token)
    {
        using var image = SKBitmap.Decode(source) ?? throw new InvalidDataException("ImageLayers.InvalidImage");
        using var surface = SKSurface.Create(new SKImageInfo(crop.Width, crop.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var path = new SKPath { FillType = SKPathFillType.EvenOdd };
        foreach (var ring in LayerContours.Trace(mask, width))
        {
            token.ThrowIfCancellationRequested();
            var points = LayerContourSmoothing.Smooth(ring);
            path.AddPoly(points.Select(point => new SKPoint(point.X - crop.X, point.Y - crop.Y)).ToArray(), close: true);
        }
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.ClipPath(path, antialias: true);
        canvas.DrawBitmap(image, 0, 0);
        using var snapshot = surface.Snapshot();
        using var encoded = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }
}
