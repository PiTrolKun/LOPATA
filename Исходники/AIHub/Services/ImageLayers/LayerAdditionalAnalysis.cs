using System.Drawing;
using System.IO;

namespace AIHub.Services.ImageLayers;

internal static class LayerAdditionalAnalysis
{
    internal static bool Valid(LayerAnalysisInput input, LayerDocument snapshot)
    {
        if (input.Extra is not { } extra) return !input.PortraitParts
            || !input.Refine && input.Depth == LayerDepth.Parts && input.MaskMethod == LayerMaskMethod.Sam2;
        if (input.Refine || input.PortraitParts && (!extra.UsesScene() || input.Depth != LayerDepth.Parts
            || input.MaskMethod != LayerMaskMethod.Sam2)) return false;
        return extra.Method switch
        {
            LayerAdditionalMethod.Matte => input.Target is Guid id && snapshot.Aggregate(id).Any
                && input.Area.Contains(snapshot.Aggregate(id).Bounds(snapshot.Source.Width)),
            LayerAdditionalMethod.Portrait => input.Target is null,
            LayerAdditionalMethod.Watershed => extra.Markers is { Length: >= 2 }
                && extra.Markers.All(mark => mark is not null && input.Area.Contains(mark.X, mark.Y))
                && extra.Markers.Any(mark => mark.Foreground) && extra.Markers.Any(mark => !mark.Foreground),
            LayerAdditionalMethod.Describe => input.Target is null && extra.Description is { Length: > 0 and <= 140 } description
                && !string.IsNullOrWhiteSpace(description) && !description.Any(char.IsControl),
            LayerAdditionalMethod.EdgeDetails => LayerEdgeDetails.Valid(input, snapshot),
            LayerAdditionalMethod.Text => input.Target is null,
            LayerAdditionalMethod.SceneReview => input.Target is null && input.Area == snapshot.Source.Frame
                && input.Depth == LayerDepth.Objects && snapshot.Layers.Values.Any(layer => layer.Mask is not null),
            LayerAdditionalMethod.CompleteFrame => input.Target is null && input.Area == snapshot.Source.Frame,
            _ => false
        };
    }

    internal static async Task<(Dictionary<Guid, ImageLayer> Plan, OutputModelUse[] Models)> PlanAsync(
        ManagedModelLibraryStore library, LayerDocument work, LayerAnalysisInput input,
        IProgress<LayerAnalysisProgress> progress, CancellationToken token)
    {
        var area = input.Area;
        var source = work.Source;
        var extra = input.Extra!;
        switch (extra.Method)
        {
            case LayerAdditionalMethod.Matte:
            {
                progress.Report(new("MatteProgress"));
                var id = input.Target!.Value;
                var shape = work.Aggregate(id);
                var matte = await LayerToolWorker.MatteAsync(source, area, shape, token);
                var reference = work.AddMatte(matte, shape, area);
                var plan = new Dictionary<Guid, ImageLayer>(work.Layers) { [id] = work.Layers[id] with { Matte = reference } };
                var geometry = work.LayerHistory(id).LastOrDefault(step => step.Models.Length > 0
                    && step.Changes.Any(change => change.Before?.Mask != change.After.Mask))?.Models ?? [];
                return (plan, [.. geometry, LayerToolWorker.PyMatting]);
            }
            case LayerAdditionalMethod.Portrait:
            {
                progress.Report(new("PortraitProgress"));
                var (shape, matte) = await LayerToolWorker.PortraitAsync(library, source, area, token);
                var reference = work.AddMatte(matte, shape, area);
                return (work.PlanAdditionalLayer(input.Russian ? "Персонаж" : "Person", shape, reference), [LayerToolWorker.ModNet]);
            }
            case LayerAdditionalMethod.Watershed:
            {
                progress.Report(new("MarkersProgress"));
                var mask = await LayerToolWorker.WatershedAsync(source, area, extra.Markers!, token);
                if (input.Target is not Guid id)
                    return (work.PlanAdditionalLayer(input.Russian ? "Объект по меткам" : "Marked object", mask), [LayerToolWorker.Watershed]);
                var scope = LayerMask.Area(source.Width, source.Height, area);
                var combined = work.Aggregate(id).Difference(scope).Union(mask);
                return (work.PlanBoundary(id, combined), [LayerToolWorker.Watershed]);
            }
            case LayerAdditionalMethod.Describe:
                return await FindAsync(library, work, area, extra.Description!, progress, token);
            case LayerAdditionalMethod.EdgeDetails:
                return await LayerEdgeDetails.PlanAsync(work, input, progress, token);
            default:
                throw new InvalidDataException("ImageLayers.InvalidProject");
        }
    }

    private static async Task<(Dictionary<Guid, ImageLayer>, OutputModelUse[])> FindAsync(
        ManagedModelLibraryStore library, LayerDocument work, Rectangle area, string description,
        IProgress<LayerAnalysisProgress> progress, CancellationToken token)
    {
        progress.Report(new("DescribeProgress"));
        var boxes = await LayerToolWorker.DescribeAsync(library, work.Source, area, description, token);
        var locate = boxes.Take(LayerAnalysisProtocol.Limit(LayerDepth.Parts)).Select((box, i) =>
            new LayerMap("match-" + i, null, boxes.Length == 1 ? description : $"{description} ({i + 1})", box)).ToArray();
        if (locate.Length == 0) throw new InvalidDataException("ImageLayers.NoResult");
        progress.Report(new("Masking"));
        var masks = await LayerSamBatch.RunAsync(library, work.Source, area, locate, token);
        var layers = locate.Select((item, index) => (item.Name, masks[index], (LayerMatteReference?)null));
        return (work.PlanAdditionalLayers(layers), [LayerToolWorker.Florence, LayerSamBatch.Model]);
    }
}
