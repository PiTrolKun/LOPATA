using System.IO;
using System.Security.Cryptography;

namespace AIHub.Services.ImageLayers;

internal sealed partial class LayerDocument
{
    internal Guid FrameBackgroundId => new(SHA256.HashData(Id.ToByteArray()).AsSpan(0, 16));

    internal Dictionary<Guid, ImageLayer> PlanFrameBackground(Dictionary<Guid, ImageLayer> plan, bool russian,
        CancellationToken token)
    {
        var backgroundId = FrameBackgroundId;
        var foreground = Empty;
        foreach (var layer in plan.Values.Where(layer => layer.Id != backgroundId && layer.Mask is not null))
        {
            token.ThrowIfCancellationRequested();
            var pixels = Direct(layer);
            if (foreground.Meets(pixels)) throw new InvalidDataException("ImageLayers.FrameCoverageFailed");
            foreground = foreground.Union(pixels);
        }

        var visible = Source.VisibleMask(token);
        var remainder = visible.Difference(foreground);
        if (plan.TryGetValue(backgroundId, out var current))
            plan[backgroundId] = current with { Mask = Remember(remainder) };
        else if (remainder.Any)
            plan.Add(backgroundId, new(backgroundId, null, russian ? "Фон" : "Background", Remember(remainder)));

        var assignedBackground = plan.GetValueOrDefault(backgroundId) is { } background ? Direct(background) : Empty;
        if (foreground.Meets(assignedBackground) || visible.Difference(foreground.Union(assignedBackground)).Any)
            throw new InvalidDataException("ImageLayers.FrameCoverageFailed");
        return plan;
    }
}
