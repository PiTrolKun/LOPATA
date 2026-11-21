using System.Drawing;
using System.IO;
using System.Text.Json;

namespace AIHub.Services.ImageLayers;

internal static class LayerSamBatch
{
    internal static OutputModelUse Model => new(ManagedModelCatalog.ImageLayerSam2Id, ManagedModelCatalog.ImageLayerSam2Name,
        ManagedModelCatalog.ImageLayerSam2Revision, "pixel segmentation");

    internal static async Task<LayerMask[]> RunAsync(ManagedModelLibraryStore library, LayerSource source, Rectangle area,
        LayerMap[] targets, CancellationToken token)
    {
        var model = await ManagedModelRuntimeAudit.VerifyAsync(library, ManagedModelCatalog.ImageLayerSam2Id, token);
        var runtime = await ManagedPythonRuntime.ResolveAsync("cpu", token, probeCpuWithTorch: false);
        using var worker = typeof(LayerSamBatch).Assembly.GetManifestResourceStream("AIHub.ImageLayers.Sam2")
            ?? throw new InvalidOperationException("ImageLayers.Components");
        using var reader = new StreamReader(worker);
        var code = await reader.ReadToEndAsync(token);
        var folder = Directory.CreateTempSubdirectory("lopata-layer-masks-").FullName;
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(folder, "source.png"), source.Encode(area, forModel: false), token);
            await File.WriteAllTextAsync(Path.Combine(folder, "boxes.json"),
                JsonSerializer.Serialize(targets.Select(target => target.Box)), token);
            await LayerRasterRuntime.RunAsync(runtime.Python, folder, code, [folder, model.InstallDirectory], token, "ImageLayers.Sam2");
            return await LayerRasterBatch.ReadMasksAsync(source, area, folder, targets.Length, token);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
