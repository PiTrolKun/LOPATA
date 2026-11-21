using System.Drawing;
using System.IO;
using System.Text.Json;

namespace AIHub.Services.ImageLayers;

internal static partial class LayerToolWorker
{
    internal static OutputModelUse MediaPipe => new(ManagedModelCatalog.ImageLayerMediaPipeId,
        ManagedModelCatalog.ImageLayerMediaPipeName, ManagedModelCatalog.ImageLayerMediaPipeRevision, "person part segmentation");
    internal static OutputModelUse ModNet => new(ManagedModelCatalog.ImageLayerModNetId,
        ManagedModelCatalog.ImageLayerModNetName, ManagedModelCatalog.ImageLayerModNetRevision, "portrait matting");
    internal static OutputModelUse Florence => new(ManagedModelCatalog.ImageLayerFlorenceId,
        ManagedModelCatalog.ImageLayerFlorenceName, ManagedModelCatalog.ImageLayerFlorenceRevision, "phrase grounding");
    internal static OutputModelUse PyMatting => new(LayerRasterRuntime.PortraitLicenseId, "PyMatting closed-form", "1.1.16", "edge transparency");
    internal static OutputModelUse Watershed => new(LayerRasterRuntime.LicenseId, "OpenCV Watershed", "4.10.0", "marker segmentation");

    internal static async Task<byte[][]> PartsAsync(ManagedModelLibraryStore library, LayerSource source,
        Rectangle[] crops, CancellationToken token)
    {
        var card = await ManagedModelRuntimeAudit.VerifyAsync(library, MediaPipe.Id, token);
        var model = Path.Combine(card.InstallDirectory, ManagedModelCatalog.ImageLayerMediaPipeFile);
        return await InWorkspaceAsync(async folder =>
        {
            var names = crops.Select((_, index) => $"crop-{index}.png").ToArray();
            for (var index = 0; index < crops.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                await File.WriteAllBytesAsync(Path.Combine(folder, names[index]), source.Encode(crops[index], forModel: false), token);
            }
            await File.WriteAllTextAsync(Path.Combine(folder, "crops.json"), JsonSerializer.Serialize(names), token);
            await RunPortraitAsync(folder, "parts", token, model);
            var result = new byte[crops.Length][];
            for (var index = 0; index < result.Length; index++)
            {
                var path = Path.Combine(folder, $"categories-{index}.bin");
                var crop = crops[index];
                if (new FileInfo(path).Length != (long)crop.Width * crop.Height)
                    throw new InvalidDataException("ImageLayers.RasterFailed");
                result[index] = await File.ReadAllBytesAsync(path, token);
                if (result[index].Any(category => category > 5)) throw new InvalidDataException("ImageLayers.RasterFailed");
            }
            return result;
        });
    }

    internal static Task<LayerMatte> MatteAsync(LayerSource source, Rectangle crop, LayerMask mask, CancellationToken token) =>
        InWorkspaceAsync(async folder =>
        {
            await WriteSourceAsync(folder, source, crop, token);
            var local = LayerMask.Build(crop.Width * crop.Height, bits =>
            {
                for (var y = 0; y < crop.Height; y++)
                    for (var x = 0; x < crop.Width; x++)
                        bits[y * crop.Width + x] = mask[(crop.Top + y) * source.Width + crop.Left + x];
            });
            await File.WriteAllBytesAsync(Path.Combine(folder, "input.bits"), local.Pack(), token);
            await RunPortraitAsync(folder, "matte", token);
            if (!File.Exists(Path.Combine(folder, "cutout.png"))) throw new InvalidDataException("ImageLayers.NoSoftEdge");
            return await ReadMatteAsync(folder, token);
        });

    internal static async Task<(LayerMask Mask, LayerMatte Matte)> PortraitAsync(ManagedModelLibraryStore library,
        LayerSource source, Rectangle crop, CancellationToken token)
    {
        var card = await ManagedModelRuntimeAudit.VerifyAsync(library, ModNet.Id, token);
        var model = Path.Combine(card.InstallDirectory, ManagedModelCatalog.ImageLayerModNetFile);
        return await InWorkspaceAsync(async folder =>
        {
            await WriteSourceAsync(folder, source, crop, token);
            await RunPortraitAsync(folder, "portrait", token, model);
            var masks = await LayerRasterBatch.ReadMasksAsync(source, crop, folder, 1, token);
            if (!masks[0].Any) throw new InvalidDataException("ImageLayers.NoResult");
            return (masks[0], await ReadMatteAsync(folder, token));
        });
    }

    internal static Task<LayerMask> WatershedAsync(LayerSource source, Rectangle crop, LayerMarker[] markers, CancellationToken token) =>
        InWorkspaceAsync(async folder =>
        {
            // Keep a narrow background collar for the flood, including OpenCV's reserved outer pixel.
            var context = Rectangle.Intersect(source.Frame, Rectangle.Inflate(crop, 2, 2));
            await WriteSourceAsync(folder, source, context, token);
            var points = markers.Select(marker => new object[] { marker.X - context.Left, marker.Y - context.Top, marker.Foreground });
            var area = new[] { crop.Left - context.Left, crop.Top - context.Top, crop.Right - context.Left, crop.Bottom - context.Top };
            await File.WriteAllTextAsync(Path.Combine(folder, "markers.json"), JsonSerializer.Serialize(new { area, points }), token);
            await RunPortraitAsync(folder, "watershed", token);
            var masks = await LayerRasterBatch.ReadMasksAsync(source, context, folder, 1, token);
            if (!masks[0].Any) throw new InvalidDataException("ImageLayers.NoResult");
            return masks[0];
        });

    internal static async Task<int[][]> DescribeAsync(ManagedModelLibraryStore library, LayerSource source,
        Rectangle crop, string description, CancellationToken token)
    {
        var card = await ManagedModelRuntimeAudit.VerifyAsync(library, Florence.Id, token);
        var runtime = await ManagedPythonRuntime.ResolveAsync("cpu", token, probeCpuWithTorch: false);
        return await InWorkspaceAsync(async folder =>
        {
            await File.WriteAllBytesAsync(Path.Combine(folder, "source.jpg"), source.Encode(crop, forModel: true), token);
            await File.WriteAllTextAsync(Path.Combine(folder, "description.txt"), description, token);
            var code = await CodeAsync("Florence", token);
            await LayerRasterRuntime.RunAsync(runtime.Python, folder, code, [folder, card.InstallDirectory], token, "ImageLayers.Florence");
            var path = Path.Combine(folder, "boxes.json");
            if (new FileInfo(path).Length > 64 * 1024) throw new InvalidDataException("ImageLayers.InvalidReply");
            var boxes = JsonSerializer.Deserialize<int[][]>(await File.ReadAllBytesAsync(path, token));
            if (boxes is null || boxes.Any(box => box is not { Length: 4 } || !LayerAnalysisProtocol.ValidBox(box)))
                throw new InvalidDataException("ImageLayers.InvalidReply");
            return boxes;
        });
    }

    private static Task WriteSourceAsync(string folder, LayerSource source, Rectangle crop, CancellationToken token) =>
        File.WriteAllBytesAsync(Path.Combine(folder, "source.png"), source.Encode(crop, forModel: false), token);

    private static async Task<LayerMatte> ReadMatteAsync(string folder, CancellationToken token)
    {
        var path = Path.Combine(folder, "cutout.png");
        if (new FileInfo(path).Length > LayerMatte.MaxBytes) throw new InvalidDataException("ImageLayers.ProjectTooLarge");
        return LayerMatte.Load(await File.ReadAllBytesAsync(path, token));
    }

    private static async Task RunPortraitAsync(string folder, string action, CancellationToken token, params string[] assets)
    {
        var full = action != "watershed";
        if (!await LayerRasterRuntime.PrepareAsync(false, null, token, portrait: full))
            throw new InvalidDataException("ImageLayers.Components");
        var code = await CodeAsync("Portrait", token);
        await LayerRasterRuntime.RunAsync(full ? LayerRasterRuntime.PortraitPython : LayerRasterRuntime.Python,
            folder, code, [action, folder, .. assets], token, $"ImageLayers.{action}", portrait: full);
    }

    private static async Task<string> CodeAsync(string name, CancellationToken token)
    {
        using var stream = typeof(LayerToolWorker).Assembly.GetManifestResourceStream($"AIHub.ImageLayers.{name}")
            ?? throw new InvalidDataException("ImageLayers.Components");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(token);
    }

    private static async Task<T> InWorkspaceAsync<T>(Func<string, Task<T>> action)
    {
        var folder = Directory.CreateTempSubdirectory("lopata-layer-tool-").FullName;
        try { return await action(folder); }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
