using System.Drawing;
using System.IO;
using System.Text.Json;

namespace AIHub.Services.ImageLayers;

internal sealed record LayerRasterJob(LayerMap Layer, IReadOnlyList<LayerRasterPiece> Pieces, LayerMask? First = null);

internal static class LayerRasterBatch
{
    internal static async Task<LayerMask[]> RunAsync(LayerSource source, Rectangle area, LayerRasterJob[] jobs, CancellationToken token)
    {
        if (jobs.Length == 0) return [];
        using var worker = typeof(LayerRasterBatch).Assembly.GetManifestResourceStream("AIHub.ImageLayers.Raster")
            ?? throw new InvalidOperationException("ImageLayers.Components");
        using var reader = new StreamReader(worker);
        var code = await reader.ReadToEndAsync(token);
        var folder = Directory.CreateTempSubdirectory("lopata-layers-").FullName;
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(folder, "source.png"), source.Encode(area, forModel: false), token);
            var request = jobs.Select(job => new
            {
                crossCheck = job.First is not null,
                pieces = job.Pieces.Select(piece => new
                {
                    crop = new[] { piece.Frame.Left - area.Left, piece.Frame.Top - area.Top, piece.Frame.Width, piece.Frame.Height },
                    components = piece.Components.Select(shape => new { outer = shape.Outer, holes = shape.Holes, open = shape.Open })
                })
            }).ToArray();
            await File.WriteAllTextAsync(Path.Combine(folder, "request.json"), JsonSerializer.Serialize(request), token);
            for (var i = 0; i < jobs.Length; i++)
            {
                if (jobs[i].First is not { } first) continue;
                var crop = LayerMask.Build(area.Width * area.Height, bits =>
                {
                    for (var y = 0; y < area.Height; y++)
                        for (var x = 0; x < area.Width; x++)
                            if (first[(area.Y + y) * source.Width + area.X + x]) bits[y * area.Width + x] = true;
                });
                await File.WriteAllBytesAsync(Path.Combine(folder, $"input-{i}.bits"), crop.Pack(), token);
            }
            await LayerRasterRuntime.RunAsync(LayerRasterRuntime.Python, folder, code, [folder], token);
            return await ReadMasksAsync(source, area, folder, jobs.Length, token);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    internal static async Task<LayerMask[]> ReadMasksAsync(LayerSource source, Rectangle area, string folder,
        int numberOfMasks, CancellationToken token)
    {
        var masks = new LayerMask[numberOfMasks];
        var count = checked(area.Width * area.Height);
        for (var i = 0; i < masks.Length; i++)
        {
            var path = Path.Combine(folder, $"output-{i}.bits");
            if (!File.Exists(path) || new FileInfo(path).Length != (count + 7L) / 8)
                throw new InvalidDataException("ImageLayers.RasterFailed");
            var local = LayerMask.Unpack(await File.ReadAllBytesAsync(path, token), count);
            masks[i] = area == source.Frame ? local : LayerMask.Build(source.Width * source.Height, bits =>
            {
                for (var y = 0; y < area.Height; y++)
                    for (var x = 0; x < area.Width; x++)
                        if (local[y * area.Width + x]) bits[(area.Y + y) * source.Width + area.X + x] = true;
            });
        }
        return masks;
    }
}
