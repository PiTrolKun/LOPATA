using System.IO;
using Lopata.Updates;

namespace AIHub.Services.ImageLayers;

internal static class LayerMatteExport
{
    internal static void Save(LayerDocument document, Guid layer, string path, CancellationToken token)
    {
        if (document.ActiveMatte(layer) is not { } cutout) throw new InvalidDataException("ImageLayers.NoSoftEdge");
        var step = document.History.LastOrDefault(step => step.Kind is LayerAdditionalMethods.Matte or LayerAdditionalMethods.Portrait or LayerAdditionalMethods.EdgeDetails
            && step.Changes.Any(change => change.Id == layer && change.After.Matte == cutout.Reference && change.Before?.Matte != cutout.Reference));
        var facts = new OutputProvenance
        {
            Scenario = "image-layers/cutout", CreatedUtc = step?.AtUtc,
            ModelHistory = step is null ? "unknown" : "recorded",
            Models = step?.Models ?? []
        };
        token.ThrowIfCancellationRequested();
        var full = Path.GetFullPath(path);
        SafeUpdatePath.RejectLinks(full);
        var temp = Path.Combine(Path.GetDirectoryName(full)!, "." + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(cutout.Image.Png);
                file.Flush(true);
            }
            OutputImageMetadata.Write(temp, "png", cutout.Image.Width, cutout.Image.Height, facts);
            token.ThrowIfCancellationRequested();
            File.Move(temp, full, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
